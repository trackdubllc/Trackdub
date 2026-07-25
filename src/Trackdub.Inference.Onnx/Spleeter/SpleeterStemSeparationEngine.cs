using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Globalization;

namespace Trackdub.Inference.Onnx.Spleeter;

public sealed class SpleeterStemSeparationEngine : IStemSeparationEngineAdapter, IStageRuntimeExecutionReporter
{
    public const string EngineFamilyName = "spleeter";
    private const string VocalsModelFileName = "vocals.onnx";
    private const string AccompanimentModelFileName = "accompaniment.onnx";
    private const int TargetSampleRate = 44100;

    private readonly ISpleeterSeparator separator;
    private readonly IRuntimePlanner? runtimePlanner;
    private readonly IRuntimePlanningPreferences? runtimePlanningPreferences;

    public SpleeterStemSeparationEngine()
        : this(new SpleeterOnnxSeparator(), runtimePlanner: null, runtimePlanningPreferences: null)
    {
    }

    public SpleeterStemSeparationEngine(IRuntimePlanner runtimePlanner)
        : this(new SpleeterOnnxSeparator(), runtimePlanner, runtimePlanningPreferences: null)
    {
    }

    public SpleeterStemSeparationEngine(
        IRuntimePlanner runtimePlanner,
        IRuntimePlanningPreferences? runtimePlanningPreferences)
        : this(new SpleeterOnnxSeparator(), runtimePlanner, runtimePlanningPreferences)
    {
    }

    internal SpleeterStemSeparationEngine(ISpleeterSeparator separator)
        : this(separator, runtimePlanner: null, runtimePlanningPreferences: null)
    {
    }

    internal SpleeterStemSeparationEngine(
        ISpleeterSeparator separator,
        IRuntimePlanningPreferences? runtimePlanningPreferences)
        : this(separator, runtimePlanner: null, runtimePlanningPreferences)
    {
    }

    private SpleeterStemSeparationEngine(
        ISpleeterSeparator separator,
        IRuntimePlanner? runtimePlanner,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    {
        this.separator = separator ?? throw new ArgumentNullException(nameof(separator));
        this.runtimePlanner = runtimePlanner;
        this.runtimePlanningPreferences = runtimePlanningPreferences;
    }

    public string EngineFamily => EngineFamilyName;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<StemSeparationResult> SeparateAsync(
        StemSeparationRequest request,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (runtimePlanner is null)
        {
            throw new InvalidOperationException("Spleeter separation requires a planned runtime.");
        }

        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.Separation,
                request.PreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: request.PreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);

        return await SeparateAsync(request, plan, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StemSeparationResult> SeparateAsync(
        StemSeparationRequest request,
        StageRuntimePlan plan,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        EnsurePlanReady(plan);

        string modelRootPath = ResolveModelRootPath(plan);
        LastExecutionSummary = CreateExecutionSummary(plan, FormatProvider(plan.ExecutionProvider ?? ExecutionProviderKind.Cpu), "Spleeter ONNX session creation pending.");

        try
        {
            using IAudioChannelSamples sourceAudio = await WaveAudioReader
                .ReadPcm16Async(request.SourceAudioPath, cancellationToken)
                .ConfigureAwait(false);

            if (sourceAudio.SampleRate != TargetSampleRate)
            {
                throw new InvalidOperationException(
                    $"Spleeter separation requires {TargetSampleRate}Hz audio, but found {sourceAudio.SampleRate}Hz.");
            }

            int sampleCount = checked((int)sourceAudio.SampleFrameCount);
            float[] left = new float[sampleCount];
            float[] right = new float[sampleCount];
            ReadStereoSamples(sourceAudio, left, right);

            SpleeterSeparation separated = await separator
                .SeparateAsync(
                    new SpleeterSeparatorRequest(modelRootPath, plan, left, right, sourceAudio.SampleRate),
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            left = []; // release large input buffers before async output serialization
            right = [];
            await WriteOutputsAsync(request, separated, cancellationToken).ConfigureAwait(false);

            IReadOnlyDictionary<string, string> metadata = BuildMetadata(plan, modelRootPath, separated);
            LastExecutionSummary = CreateExecutionSummary(
                plan,
                metadata.TryGetValue("selected_provider", out string? selectedProvider) ? selectedProvider : FormatProvider(plan.ExecutionProvider ?? ExecutionProviderKind.Cpu),
                metadata.TryGetValue("bootstrap_detail", out string? bootstrapDetail) ? bootstrapDetail : "Spleeter ONNX separation completed.");

            return new StemSeparationResult(
                DurationSeconds: sampleCount / (double)separated.SampleRate,
                SampleRate: separated.SampleRate,
                ChannelCount: 1,
                Metadata: metadata);
        }
        catch (Exception ex)
        {
            DeleteOutputPaths(request);
            throw new InvalidOperationException($"Spleeter separation failed: {ex.Message}", ex);
        }
    }

    private static void EnsurePlanReady(StageRuntimePlan plan)
    {
        if (!plan.IsRunnable())
        {
            throw new InvalidOperationException(
                plan.Fallback?.Detail ?? "Spleeter separation runtime is not ready.");
        }

        if (!string.Equals(plan.EngineFamily, EngineFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Spleeter separation cannot run engine family '{plan.EngineFamily ?? "unknown"}'.");
        }
    }

    private static string ResolveModelRootPath(StageRuntimePlan plan)
    {
        string? envModelPath = Environment.GetEnvironmentVariable("TRACKDUB_SPLEETER_ONNX_PATH");
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

        throw new FileNotFoundException("Spleeter model directory unavailable.");
    }

    private static void ReadStereoSamples(IAudioChannelSamples sourceAudio, Span<float> left, Span<float> right)
    {
        if (sourceAudio.ChannelCount <= 1)
        {
            sourceAudio.ReadMonoSamples(0, left);
            left.CopyTo(right);
            return;
        }

        sourceAudio.ReadChannelSamples(0, 0, left);
        sourceAudio.ReadChannelSamples(0, Math.Min(1, sourceAudio.ChannelCount - 1), right);
    }

    private static async Task WriteOutputsAsync(
        StemSeparationRequest request,
        SpleeterSeparation separated,
        CancellationToken cancellationToken)
    {
        await WaveAudioWriter.WriteMonoPcm16Async(
            request.VocalsOutputPath,
            separated.Vocals,
            separated.SampleRate,
            cancellationToken).ConfigureAwait(false);

        await WaveAudioWriter.WriteMonoPcm16Async(
            request.AmbianceOutputPath,
            separated.Accompaniment,
            separated.SampleRate,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.MusicOutputPath))
        {
            await WaveAudioWriter.WriteMonoPcm16Async(
                request.MusicOutputPath,
                separated.Accompaniment,
                separated.SampleRate,
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.SoundEffectsOutputPath))
        {
            DeleteIfExists(request.SoundEffectsOutputPath);
        }

        if (request.RawStemOutputPaths is not null)
        {
            await WriteRawStemAsync(request.RawStemOutputPaths, "vocals", separated.Vocals, separated.SampleRate, cancellationToken)
                .ConfigureAwait(false);
            await WriteRawStemAsync(request.RawStemOutputPaths, "other", separated.Accompaniment, separated.SampleRate, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Task WriteRawStemAsync(
        IReadOnlyDictionary<string, string> rawStemOutputPaths,
        string stemName,
        float[] samples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        return rawStemOutputPaths.TryGetValue(stemName, out string? outputPath)
            ? WaveAudioWriter.WriteMonoPcm16Async(outputPath, samples, sampleRate, cancellationToken)
            : Task.CompletedTask;
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(
        StageRuntimePlan plan,
        string modelRootPath,
        SpleeterSeparation separated)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runner"] = "onnx",
            ["engine_family"] = EngineFamilyName,
            ["model"] = plan.ModelAlias ?? string.Empty,
            ["model_root"] = modelRootPath,
            ["selected_provider"] = FormatProvider(plan.ExecutionProvider ?? ExecutionProviderKind.Cpu),
            ["variant"] = plan.Variant ?? "default",
            ["chunk_count"] = separated.ChunkCount.ToString(CultureInfo.InvariantCulture)
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

    private static void DeleteOutputPaths(StemSeparationRequest request)
    {
        DeleteIfExists(request.VocalsOutputPath);
        DeleteIfExists(request.AmbianceOutputPath);
        if (!string.IsNullOrWhiteSpace(request.MusicOutputPath))
        {
            DeleteIfExists(request.MusicOutputPath);
        }
        if (!string.IsNullOrWhiteSpace(request.SoundEffectsOutputPath))
        {
            DeleteIfExists(request.SoundEffectsOutputPath);
        }
        if (request.RawStemOutputPaths is not null)
        {
            foreach (string outputPath in request.RawStemOutputPaths.Values)
            {
                DeleteIfExists(outputPath);
            }
        }
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
