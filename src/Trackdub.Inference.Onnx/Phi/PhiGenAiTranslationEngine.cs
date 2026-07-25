using Microsoft.ML.OnnxRuntimeGenAI;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Phi;

public sealed class PhiGenAiTranslationEngine(IRuntimePlanner runtimePlanner,
    BenchmarkModelPathResolver modelPathResolver,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : ITranslationEngineAdapter, IStageRuntimeExecutionReporter
{
    public const string EngineFamilyName = "phi-genai";

    private const string GenAiConfigFileName = "genai_config.json";
    private const int MaxNewTokens = 512;
    private const double RepetitionPenalty = 1.1;

    private static readonly string[] EosTokenSuffixes =
        ["<|end|>", "<|endoftext|>", "<|assistant|>", "<|im_end|>"];

    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly BenchmarkModelPathResolver modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public string EngineFamily => EngineFamilyName;

    public async Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Segments);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(
            await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(new StageRuntimePlanningRequest(
                RuntimeStage.Translation,
                PreferredModelAlias: request.PreferredModelAlias,
                SourceLanguage: request.SourceLanguage,
                TargetLanguage: request.TargetLanguage,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: request.PreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken),
            cancellationToken).ConfigureAwait(false);
        EnsurePlanReady(plan);

        if (request.Segments.Count == 0)
        {
            LastExecutionSummary = CreateExecutionSummary(plan, "Translation skipped: no segments.");
            return [];
        }

        string modelRootPath = PlannedRuntimeModelResolver.ResolveModelRootPath(plan, modelPathResolver);
        EnsureGenAiModelRoot(modelRootPath);

        using Model model = CreateModel(modelRootPath, plan.ExecutionProvider!.Value);
        using Tokenizer tokenizer = new(model);

        string targetLanguageName = ResolveTargetLanguageName(request.TargetLanguage);
        var translatedSegments = new List<TranslatedTextSegment>(request.Segments.Count);

        foreach (TranslationInputSegment segment in request.Segments.OrderBy(static s => s.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string translatedText = TranslateSegment(model, tokenizer, segment.Text, targetLanguageName, cancellationToken);
            translatedSegments.Add(new TranslatedTextSegment(
                segment.Index,
                segment.StartSeconds,
                segment.EndSeconds,
                translatedText));
        }

        LastExecutionSummary = CreateExecutionSummary(plan, "ONNX Runtime GenAI Phi text generation.");
        return translatedSegments;
    }

    private static string TranslateSegment(
        Model model,
        Tokenizer tokenizer,
        string sourceText,
        string targetLanguageName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return string.Empty;
        }

        string prompt = BuildTranslationPrompt(sourceText, targetLanguageName);
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

        // Capture prompt length before generation to decode only new tokens.
        int promptLength = (int)generator.GetSequence(0).Length;

        while (!generator.IsDone())
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
        }

        ReadOnlySpan<int> fullSequence = generator.GetSequence(0);
        string decoded = tokenizer.Decode(fullSequence[promptLength..]);
        return CleanDecodedTranslation(decoded);
    }

    internal static string CleanDecodedTranslation(string decoded)
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

    private static string BuildTranslationPrompt(string sourceText, string targetLanguageName) =>
        $"<|system|>\nTranslate the input to {targetLanguageName}. Output only the translation, no explanation.<|end|>\n<|user|>\n{sourceText}<|end|>\n<|assistant|>\n";

    private static string ResolveTargetLanguageName(string targetLanguageCode) =>
        targetLanguageCode.Trim().ToLowerInvariant() switch
        {
            "en" => "English",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "el" => "Greek",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ja" => "Japanese",
            "zh" => "Chinese",
            "ko" => "Korean",
            "ar" => "Arabic",
            "ru" => "Russian",
            "nl" => "Dutch",
            "pl" => "Polish",
            "tr" => "Turkish",
            "hi" => "Hindi",
            var code => code
        };

    private static void EnsurePlanReady(StageRuntimePlan plan)
    {
        if (plan.IsRunnable() && plan.ExecutionProvider is not null && !string.IsNullOrWhiteSpace(plan.ModelAlias))
        {
            return;
        }

        throw new InvalidOperationException(
            plan.Fallback?.Detail ?? "Runtime planner did not produce a ready translation plan for Phi GenAI.");
    }

    private static void EnsureGenAiModelRoot(string modelRootPath)
    {
        string configPath = Path.Combine(modelRootPath, GenAiConfigFileName);
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "Phi GenAI model root does not contain genai_config.json.", configPath);
        }
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
            _ => throw new ArgumentOutOfRangeException(nameof(executionProvider), executionProvider, "Unsupported GenAI execution provider.")
        };

    private static StageRuntimeExecutionSummary CreateExecutionSummary(
        StageRuntimePlan plan,
        string bootstrapDetail) =>
        new(
            "auto",
            plan.ExecutionProvider!.Value.ToString().ToLowerInvariant(),
            plan.ModelId,
            plan.ModelAlias,
            plan.Variant,
            plan.Fallback is null ? bootstrapDetail : $"{bootstrapDetail} {plan.Fallback.Detail}");
}
