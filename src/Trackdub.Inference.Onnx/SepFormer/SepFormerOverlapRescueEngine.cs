using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using System.Globalization;

namespace Trackdub.Inference.Onnx.SepFormer;

public sealed class SepFormerOverlapRescueEngine : IOverlapRescueEngineAdapter, IStageRuntimeExecutionReporter
{
    public const string EngineFamilyName = "sepformer";
    private const int TargetSampleRate = 16000;

    private readonly ISepFormerSeparator separator;
    private readonly IRuntimePlanner? runtimePlanner;
    private readonly IRuntimePlanningPreferences? runtimePlanningPreferences;

    public SepFormerOverlapRescueEngine()
        : this(new SepFormerOnnxSeparator(), runtimePlanner: null, runtimePlanningPreferences: null)
    {
    }

    public SepFormerOverlapRescueEngine(IRuntimePlanner runtimePlanner)
        : this(new SepFormerOnnxSeparator(), runtimePlanner, runtimePlanningPreferences: null)
    {
    }

    public SepFormerOverlapRescueEngine(
        IRuntimePlanner runtimePlanner,
        IRuntimePlanningPreferences? runtimePlanningPreferences)
        : this(new SepFormerOnnxSeparator(), runtimePlanner, runtimePlanningPreferences)
    {
    }

    internal SepFormerOverlapRescueEngine(ISepFormerSeparator separator)
        : this(separator, runtimePlanner: null, runtimePlanningPreferences: null)
    {
    }

    internal SepFormerOverlapRescueEngine(
        ISepFormerSeparator separator,
        IRuntimePlanningPreferences? runtimePlanningPreferences)
        : this(separator, runtimePlanner: null, runtimePlanningPreferences)
    {
    }

    private SepFormerOverlapRescueEngine(
        ISepFormerSeparator separator,
        IRuntimePlanner? runtimePlanner,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    {
        this.separator = separator ?? throw new ArgumentNullException(nameof(separator));
        this.runtimePlanner = runtimePlanner;
        this.runtimePlanningPreferences = runtimePlanningPreferences;
    }

    public string EngineFamily => EngineFamilyName;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<OverlapRescueResult> RescueAsync(
        OverlapRescueRequest request,
        IProgress<OverlapRescueProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (runtimePlanner is null)
        {
            throw new InvalidOperationException("SepFormer overlap rescue requires a planned runtime.");
        }

        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.OverlapRescue,
                request.PreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: request.PreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);
        return await RescueAsync(request, plan, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OverlapRescueResult> RescueAsync(
        OverlapRescueRequest request,
        StageRuntimePlan plan,
        IProgress<OverlapRescueProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        EnsurePlanReady(plan);

        string modelRootPath = ResolveModelRootPath(plan);
        LastExecutionSummary = CreateExecutionSummary(
            plan,
            FormatProvider(plan.ExecutionProvider ?? ExecutionProviderKind.Cpu),
            "SepFormer overlap rescue session creation pending.");

        try
        {
            using IAudioSamples regionAudio = await WaveAudioReader
                .ReadMonoPcm16Async(request.RegionAudioPath, cancellationToken)
                .ConfigureAwait(false);

            int sampleCount = checked((int)regionAudio.SampleFrameCount);
            float[] samples = new float[sampleCount];
            regionAudio.ReadMonoSamples(0, samples);

            if (regionAudio.SampleRate != TargetSampleRate)
            {
                samples = AudioResampler.Resample(samples, regionAudio.SampleRate, TargetSampleRate);
                sampleCount = samples.Length;
            }

            SepFormerSeparation separated = await separator
                .SeparateRegionAsync(
                    new SepFormerRegionRequest(modelRootPath, plan, samples, TargetSampleRate),
                    cancellationToken)
                .ConfigureAwait(false);

            await WaveAudioWriter.WriteMonoPcm16Async(
                request.SourceCandidate0OutputPath,
                separated.Source0,
                separated.SampleRate,
                cancellationToken).ConfigureAwait(false);
            await WaveAudioWriter.WriteMonoPcm16Async(
                request.SourceCandidate1OutputPath,
                separated.Source1,
                separated.SampleRate,
                cancellationToken).ConfigureAwait(false);

            IReadOnlyDictionary<string, string> metadata = BuildMetadata(plan, modelRootPath, separated);
            LastExecutionSummary = CreateExecutionSummary(
                plan,
                metadata.TryGetValue("selected_provider", out string? selectedProvider)
                    ? selectedProvider
                    : FormatProvider(plan.ExecutionProvider ?? ExecutionProviderKind.Cpu),
                metadata.TryGetValue("bootstrap_detail", out string? bootstrapDetail)
                    ? bootstrapDetail
                    : "SepFormer overlap rescue completed.");

            return new OverlapRescueResult(
                DurationSeconds: sampleCount / (double)separated.SampleRate,
                SampleRate: separated.SampleRate,
                ChannelCount: 1,
                PermutationWarning: separated.PermutationWarning,
                Metadata: metadata);
        }
        catch (Exception ex)
        {
            DeleteOutputPaths(request);
            throw new InvalidOperationException("SepFormer overlap rescue failed.", ex);
        }
    }

    private static void EnsurePlanReady(StageRuntimePlan plan)
    {
        if (!plan.IsRunnable())
        {
            throw new InvalidOperationException(
                plan.Fallback?.Detail ?? "SepFormer overlap rescue runtime is not ready.");
        }

        if (!string.Equals(plan.EngineFamily, EngineFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SepFormer overlap rescue cannot run engine family '{plan.EngineFamily ?? "unknown"}'.");
        }
    }

    private static string ResolveModelRootPath(StageRuntimePlan plan)
    {
        string? envModelPath = Environment.GetEnvironmentVariable("TRACKDUB_SEPFORMER_ONNX_PATH");
        if (!string.IsNullOrWhiteSpace(envModelPath) && Directory.Exists(envModelPath))
        {
            return Path.GetFullPath(envModelPath);
        }

        if (!string.IsNullOrWhiteSpace(plan.ModelEntryPath))
        {
            string root = Path.GetDirectoryName(plan.ModelEntryPath) ?? string.Empty;
            if (Directory.Exists(root))
            {
                return Path.GetFullPath(root);
            }
        }

        throw new FileNotFoundException("SepFormer model directory unavailable.");
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(
        StageRuntimePlan plan,
        string modelRootPath,
        SepFormerSeparation separated)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runner"] = "onnx",
            ["engine_family"] = EngineFamilyName,
            ["model"] = plan.ModelAlias ?? string.Empty,
            ["model_root"] = modelRootPath,
            ["selected_provider"] = FormatProvider(plan.ExecutionProvider ?? ExecutionProviderKind.Cpu),
            ["variant"] = plan.Variant ?? "default",
            ["chunk_count"] = separated.ChunkCount.ToString(CultureInfo.InvariantCulture),
            ["permutation_warning"] = separated.PermutationWarning.ToString(CultureInfo.InvariantCulture),
            ["mode"] = "overlap-rescue"
        };

        if (separated.Metadata is not null)
        {
            foreach ((string key, string value) in separated.Metadata)
            {
                metadata[key] = value;
            }
        }

        return metadata;
    }

    private static StageRuntimeExecutionSummary CreateExecutionSummary(
        StageRuntimePlan plan,
        string selectedProvider,
        string detail) =>
        new(
            FormatProvider(plan.ExecutionProvider ?? ExecutionProviderKind.Cpu),
            selectedProvider,
            plan.ModelId ?? string.Empty,
            plan.ModelAlias ?? string.Empty,
            plan.Variant,
            detail);

    private static string FormatProvider(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.Migraphx => "migraphx",
            ExecutionProviderKind.TensorRTRtx => "tensorrt-rtx",
            _ => provider.ToString().ToLowerInvariant()
        };

    private static void DeleteOutputPaths(OverlapRescueRequest request)
    {
        DeleteIfExists(request.SourceCandidate0OutputPath);
        DeleteIfExists(request.SourceCandidate1OutputPath);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
