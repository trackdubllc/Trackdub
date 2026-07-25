using Microsoft.ML.OnnxRuntimeGenAI;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.QwenAssistant;

public sealed class QwenLocalAssistantEngine(
    IRuntimePlanner runtimePlanner,
    BenchmarkModelPathResolver modelPathResolver,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : ILocalAssistant
{
    private const string GenAiConfigFileName = "genai_config.json";
    private const int MaxNewTokens = 512;
    private const double RepetitionPenalty = 1.05;

    private static readonly string[] EosTokenSuffixes =
        ["<|im_end|>", "<|endoftext|>", "<|assistant|>"];

    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly BenchmarkModelPathResolver modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));

    private bool? _isAvailableCache;

    public bool IsAvailable
    {
        get
        {
            // Fast path: return cached result without async work
            if (_isAvailableCache.HasValue)
                return _isAvailableCache.Value;

            // Slow path: block synchronously on first check (ConfigureAwait(false) to avoid deadlock)
            return IsAvailableAsync().GetAwaiter().GetResult();
        }
    }

    public async Task<bool> IsAvailableAsync()
    {
        if (_isAvailableCache.HasValue)
            return _isAvailableCache.Value;

        try
        {
            StageRuntimePlan plan = await runtimePlanner.PlanAsync(
                new StageRuntimePlanningRequest(RuntimeStage.TextRefinement),
                CancellationToken.None).ConfigureAwait(false);
            _isAvailableCache = plan.IsRunnable() && plan.ExecutionProvider is not null;
            return _isAvailableCache.Value;
        }
        catch (Exception)
        {
            // Assistant is not available if planning fails for any reason.
            // Catch all exceptions to ensure IsAvailable returns false gracefully
            // rather than propagating planning/device errors to callers.
            _isAvailableCache = false;
            return false;
        }
    }

    public async Task<LocalAssistantReply> AskAsync(
        LocalAssistantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.UserMessage) && string.IsNullOrWhiteSpace(request.ContextJson))
        {
            return new LocalAssistantReply(string.Empty, WasAnswered: false, FallbackReason: "Empty request.");
        }

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(
            await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
                new StageRuntimePlanningRequest(RuntimeStage.TextRefinement),
                runtimePlanningPreferences,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (!plan.IsRunnable() || plan.ExecutionProvider is null)
        {
            return new LocalAssistantReply(
                string.Empty,
                WasAnswered: false,
                FallbackReason: plan.Fallback?.Detail ?? "Assistant model is not available.");
        }

        string modelRootPath = PlannedRuntimeModelResolver.ResolveModelRootPath(plan, modelPathResolver);
        string configPath = Path.Combine(modelRootPath, GenAiConfigFileName);
        if (!File.Exists(configPath))
        {
            return new LocalAssistantReply(
                string.Empty,
                WasAnswered: false,
                FallbackReason: "Assistant model root does not contain genai_config.json.");
        }

        string prompt = QwenAssistantPromptBuilder.BuildPrompt(request);

        using Model model = CreateModel(modelRootPath, plan.ExecutionProvider.Value);
        using Tokenizer tokenizer = new(model);

        string rawOutput = GenerateText(model, tokenizer, prompt, cancellationToken);
        string cleaned = CleanDecodedOutput(rawOutput);

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return new LocalAssistantReply(string.Empty, WasAnswered: false, FallbackReason: "Model returned no answer.");
        }

        if (request.Scope != LocalAssistantScope.StarterPackAudit)
        {
            return new LocalAssistantReply(cleaned, WasAnswered: true);
        }

        bool parsed = QwenPatchOutputParser.TryParse(cleaned, out var patches);
        return parsed
            ? new LocalAssistantReply(cleaned, WasAnswered: true, ProposedPatches: patches)
            : new LocalAssistantReply(string.Empty, WasAnswered: false, FallbackReason: "Could not parse audit patches.", ProposedPatches: null);
    }

    private static string GenerateText(
        Model model,
        Tokenizer tokenizer,
        string prompt,
        CancellationToken cancellationToken)
    {
        using Sequences inputTokens = tokenizer.Encode(prompt);
        if (inputTokens.NumSequences == 0)
        {
            return string.Empty;
        }

        using GeneratorParams generatorParams = new(model);
        generatorParams.SetSearchOption("max_length", (double)(inputTokens[0].Length + MaxNewTokens));
        generatorParams.SetSearchOption("repetition_penalty", RepetitionPenalty);

        using Generator generator = new(model, generatorParams);
        generator.AppendTokenSequences(inputTokens);

        int promptLength = (int)generator.GetSequence(0).Length;
        while (!generator.IsDone())
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
        }

        ReadOnlySpan<int> fullSequence = generator.GetSequence(0);
        return tokenizer.Decode(fullSequence[promptLength..]);
    }

    private static string CleanDecodedOutput(string decoded)
    {
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return string.Empty;
        }

        string result = decoded;
        foreach (string suffix in EosTokenSuffixes)
        {
            int suffixIndex = result.IndexOf(suffix, StringComparison.Ordinal);
            if (suffixIndex >= 0)
            {
                result = result[..suffixIndex];
            }
        }

        return result.Trim();
    }

    private static Model CreateModel(string modelRootPath, ExecutionProviderKind executionProvider)
    {
        if (executionProvider is ExecutionProviderKind.Cpu)
        {
            return new Model(modelRootPath);
        }

        using Config config = new(modelRootPath);
        config.ClearProviders();
        config.AppendProvider(ToGenAiProviderName(executionProvider));
        return new Model(config);
    }

    private static string ToGenAiProviderName(ExecutionProviderKind executionProvider) =>
        executionProvider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.CoreMl => "coreml",
            ExecutionProviderKind.TensorRTRtx => "trt-rtx",
            _ => throw new ArgumentOutOfRangeException(nameof(executionProvider), executionProvider, "Unsupported GenAI execution provider.")
        };
}
