using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.LipSynthesis;

/// <summary>
/// LatentSync 1.6 ONNX lip-synthesis engine (ByteDance, openrail++ license).
/// Requires four ONNX subgraphs: UNet, VAE encoder, VAE decoder, Whisper encoder.
/// All quality gating (face detection, pose, landmarks) is handled by
/// <see cref="Trackdub.Application.LipSynthesis.LipSynthesisStageHandler"/> upstream — this
/// engine only runs when all guards have passed.
/// </summary>
public sealed class LatentSyncOnnxLipSynthesisEngine(
    IRuntimePlanner runtimePlanner,
    BenchmarkModelPathResolver modelPathResolver,
    IVideoFrameExtractor frameExtractor,
    IVideoFrameAssembler frameAssembler,
    IAudioSegmentExtractor audioExtractor,
    BundledModelManifestRegistry manifestRegistry)
    : ILipSynthesisEngine, IStageRuntimeExecutionReporter
{
    private const string EngineFamilyName = LatentSyncModelPaths.EngineFamily;
    private const int AudioSampleRateHz = 16_000;
    private const double AudioConditioningWindowSeconds = 2.0;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public bool IsAvailable
    {
        get
        {
            try
            {
                BenchmarkModelResolutionResult discovery = modelPathResolver.Discover(LatentSyncModelPaths.ManifestAlias);
                if (!string.IsNullOrWhiteSpace(discovery.Error) || discovery.Candidates.Count == 0)
                    return false;
                BenchmarkModelCandidate candidate = discovery.Candidates[0];
                string modelRoot = candidate.RootDirectory
                    ?? Path.GetDirectoryName(candidate.ModelPath)
                    ?? string.Empty;
                return LatentSyncModelPaths.AreLatentSyncFilesPresent(modelRoot);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsExperimental => IsExperimentalFromManifest(manifestRegistry);

    public string ProviderId => "latentsync-onnx";

    public string ModelId => LatentSyncModelPaths.ModelId;

    public async Task<LipSynthesisResult> SynthesizeTurnAsync(
        LipSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.LipSynthesis,
                PreferredModelAlias: request.PreferredModelAlias),
            cancellationToken)
            .ConfigureAwait(false);

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: plan.ExecutionProvider?.ToString() ?? ExecutionProviderKind.Cpu.ToString(),
            SelectedProvider: plan.ExecutionProvider?.ToString() ?? ExecutionProviderKind.Cpu.ToString(),
            ModelId: plan.ModelId ?? LatentSyncModelPaths.ModelId,
            ModelAlias: plan.ModelAlias ?? LatentSyncModelPaths.ManifestAlias,
            ModelVariant: plan.Variant,
            BootstrapDetail: "LatentSync session creation pending.");

        if (!plan.IsRunnable())
        {
            return Skipped(request,
                plan.Fallback?.Detail ?? "LatentSync runtime plan is not ready.");
        }

        if (!string.Equals(plan.EngineFamily, EngineFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(request,
                $"LatentSync engine cannot run engine family '{plan.EngineFamily ?? "unknown"}'.");
        }

        string modelRoot = PlannedRuntimeModelResolver.ResolveModelRootPath(plan, modelPathResolver);
        if (!LatentSyncModelPaths.AreLatentSyncFilesPresent(modelRoot))
            return Skipped(request, "LatentSync model files are not present.");

        ExecutionProviderKind provider = plan.ExecutionProvider ?? ExecutionProviderKind.Cpu;

        using OnnxExecutionSessionFactory.LatentSyncSessionLease lease = await OnnxExecutionSessionFactory.CreatePooledLatentSyncAsync(
            EngineFamilyName,
            LatentSyncModelPaths.UNetPath(modelRoot),
            LatentSyncModelPaths.VaeEncoderPath(modelRoot),
            LatentSyncModelPaths.VaeDecoderPath(modelRoot),
            LatentSyncModelPaths.WhisperEncoderPath(modelRoot),
            provider,
            cancellationToken)
            .ConfigureAwait(false);

        string tempDir = Path.Combine(Path.GetTempPath(), $"lipsync_{request.SegmentId:N}");
        Directory.CreateDirectory(tempDir);
        string framesDir = Path.Combine(tempDir, "frames");
        // Clear stale frames from any previous interrupted run at this deterministic path.
        if (Directory.Exists(framesDir))
            Directory.Delete(framesDir, recursive: true);
        string patchedPath = Path.Combine(tempDir, "patched.mp4");
        string? standalonePatched = null;

        try
        {
            // Extract frames from the turn window.
            FrameExtractionResult frames = await frameExtractor.ExtractTurnFramesAsync(
                request.OriginalVideoPath,
                request.TurnStart.TotalSeconds,
                request.TurnEnd.TotalSeconds,
                framesDir,
                cancellationToken)
                .ConfigureAwait(false);

            if (frames.FrameCount == 0)
                return Skipped(request, "No frames extracted for the turn.");

            // Extract the dubbed audio segment and compute mel spectrogram → Whisper embeddings.
            string segmentWav = Path.Combine(tempDir, "segment.wav");
            await audioExtractor.ExtractSegmentAsync(
                request.DubbedAudioPath,
                request.TurnStart,
                request.TurnEnd,
                segmentWav,
                cancellationToken)
                .ConfigureAwait(false);

            float[] segmentPcm = LoadPcmFromWav(segmentWav);

            var scheduler = new DdimScheduler();

            // Process each frame through the diffusion pipeline.
            string[] frameFiles = Directory.GetFiles(framesDir, "*.rgba")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (int frameIndex = 0; frameIndex < frameFiles.Length; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string framePath = frameFiles[frameIndex];
                float[] framePcm = SliceFrameAudioWindow(
                    segmentPcm,
                    frameIndex,
                    frames.FrameRate,
                    AudioConditioningWindowSeconds);
                (float[] whisperEmbeds, int whisperSeqLen, int whisperHiddenDim) = RunWhisperEncoder(lease.WhisperEncoderSession, framePcm);

                byte[] rgbaBytes = await File.ReadAllBytesAsync(framePath, cancellationToken)
                    .ConfigureAwait(false);
                int w = frames.FrameWidth;
                int h = frames.FrameHeight;

                // Encode reference frame to latent space.
                float[] normalizedFrame = LatentSyncTensorPreprocessor.RgbaToNormalizedTensor(rgbaBytes, w, h);
                float[] refLatent = RunVaeEncoder(lease.VaeEncoderSession, normalizedFrame);

                // Initialize noisy latent.
                float[] noisyLatent = CreateGaussianNoise(refLatent.Length);
                noisyLatent = scheduler.AddNoise(refLatent, noisyLatent, scheduler.Timesteps[0]);

                // DDIM denoising loop.
                foreach (int t in scheduler.Timesteps)
                {
                    float[] noise = RunUNet(lease.UNetSession, noisyLatent, t, whisperEmbeds, whisperSeqLen, whisperHiddenDim);
                    noisyLatent = scheduler.Step(noise, t, noisyLatent);
                }

                // Decode latent to pixel space and write back.
                float[] decoded = RunVaeDecoder(lease.VaeDecoderSession, noisyLatent);

                Span<byte> outRgba = rgbaBytes;
                LatentSyncTensorPreprocessor.PasteFloatTensorIntoRgba(
                    decoded, outRgba, w, h,
                    faceX: 0, faceY: 0, faceW: w, faceH: h);

                await File.WriteAllBytesAsync(framePath, rgbaBytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Assemble processed frames back to video.
            await frameAssembler.AssembleFramesAsync(
                framesDir,
                patchedPath,
                frames.FrameWidth,
                frames.FrameHeight,
                frames.FrameRate,
                cancellationToken)
                .ConfigureAwait(false);

            // Move patched clip out of tempDir so the whole working dir can be deleted below.
            standalonePatched = Path.Combine(Path.GetTempPath(), $"lipsync_patched_{request.SegmentId:N}.mp4");
            File.Move(patchedPath, standalonePatched, overwrite: true);

            LastExecutionSummary = LastExecutionSummary with
            {
                SelectedProvider = lease.SelectedProvider,
                BootstrapDetail = $"LatentSync synthesized {frames.FrameCount} frames."
            };

            return new LipSynthesisResult(
                SegmentId: request.SegmentId,
                Status: LipSynthesisEngineStatus.Synthesized,
                PatchedClipPath: standalonePatched,
                SkipReason: null,
                FailureReason: null,
                ProviderId: ProviderId,
                ModelId: ModelId);
        }
        catch (Exception)
        {
            if (standalonePatched is not null)
                try { File.Delete(standalonePatched); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static (float[] Embeddings, int SeqLen, int HiddenDim) RunWhisperEncoder(InferenceSession session, float[] pcm16000Hz)
    {
        float[] mel = LatentSyncTensorPreprocessor.ComputeWhisperMelSpectrogram(pcm16000Hz);
        (int melBins, int melFrames) = LatentSyncTensorPreprocessor.MelShape;

        var melTensor = new DenseTensor<float>(mel, [1, melBins, melFrames]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input_features", melTensor) };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.RunWithRetry(inputs);
        var hidden = outputs.Single(o => o.Name == "last_hidden_state").AsTensor<float>();
        return (hidden.ToArray(), hidden.Dimensions[1], hidden.Dimensions[2]);
    }

    internal static float[] SliceFrameAudioWindowForTest(
        float[] pcm16000Hz,
        int frameIndex,
        double frameRate,
        double windowSeconds) =>
        SliceFrameAudioWindow(pcm16000Hz, frameIndex, frameRate, windowSeconds);

    private static float[] SliceFrameAudioWindow(
        float[] pcm16000Hz,
        int frameIndex,
        double frameRate,
        double windowSeconds)
    {
        if (pcm16000Hz.Length == 0)
            return [];

        double safeFrameRate = frameRate > 0d ? frameRate : 25d;
        int windowSamples = Math.Max(1, (int)Math.Round(windowSeconds * AudioSampleRateHz));
        int startSample = (int)Math.Round(frameIndex / safeFrameRate * AudioSampleRateHz);
        startSample = Math.Clamp(startSample, 0, Math.Max(0, pcm16000Hz.Length - 1));

        var window = new float[windowSamples];
        int copyLength = Math.Min(windowSamples, pcm16000Hz.Length - startSample);
        Array.Copy(pcm16000Hz, startSample, window, 0, copyLength);
        return window;
    }

    private static float[] RunVaeEncoder(InferenceSession session, float[] normalizedFrame)
    {
        int h = LatentSyncTensorPreprocessor.TargetHeight;
        int w = LatentSyncTensorPreprocessor.TargetWidth;
        var frameTensor = new DenseTensor<float>(normalizedFrame, [1, 3, h, w]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("sample", frameTensor) };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.RunWithRetry(inputs);
        return outputs.Single(o => o.Name == "latent_sample")
            .AsTensor<float>()
            .ToArray();
    }

    private static float[] RunVaeDecoder(InferenceSession session, float[] latent)
    {
        int lh = LatentSyncTensorPreprocessor.LatentHeight;
        int lw = LatentSyncTensorPreprocessor.LatentWidth;
        int lc = LatentSyncTensorPreprocessor.LatentChannels;
        var latentTensor = new DenseTensor<float>(latent, [1, lc, lh, lw]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("latent_sample", latentTensor) };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.RunWithRetry(inputs);
        return outputs.Single(o => o.Name == "sample")
            .AsTensor<float>()
            .ToArray();
    }

    private static float[] RunUNet(
        InferenceSession session,
        float[] sample,
        int timestep,
        float[] encoderHiddenStates,
        int seqLen,
        int hiddenDim)
    {
        int lh = LatentSyncTensorPreprocessor.LatentHeight;
        int lw = LatentSyncTensorPreprocessor.LatentWidth;
        int lc = LatentSyncTensorPreprocessor.LatentChannels;

        var sampleTensor = new DenseTensor<float>(sample, [1, lc, lh, lw]);
        var timestepTensor = new DenseTensor<long>(new[] { (long)timestep }, [1]);
        var hiddenTensor = new DenseTensor<float>(encoderHiddenStates, [1, seqLen, hiddenDim]);

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("sample", sampleTensor),
            NamedOnnxValue.CreateFromTensor("timestep", timestepTensor),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hiddenTensor),
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.RunWithRetry(inputs);
        return outputs.Single(o => o.Name == "out_sample")
            .AsTensor<float>()
            .ToArray();
    }

    private static float[] LoadPcmFromWav(string wavPath)
    {
        // Minimal WAV reader: skip 44-byte header, read 16-bit LE samples, normalise to [-1, 1].
        byte[] wavBytes = File.ReadAllBytes(wavPath);
        const int HeaderBytes = 44;
        if (wavBytes.Length <= HeaderBytes)
            return [];
        int sampleCount = (wavBytes.Length - HeaderBytes) / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(wavBytes[HeaderBytes + i * 2] | (wavBytes[HeaderBytes + i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    private static float[] CreateGaussianNoise(int length)
    {
        // Box-Muller transform for Gaussian noise.
        var rng = new Random();
        float[] noise = new float[length];
        for (int i = 0; i < length - 1; i += 2)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            float mag = (float)Math.Sqrt(-2.0 * Math.Log(u1));
            noise[i] = mag * (float)Math.Cos(2.0 * Math.PI * u2);
            noise[i + 1] = mag * (float)Math.Sin(2.0 * Math.PI * u2);
        }
        if (length % 2 == 1)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            noise[length - 1] = (float)Math.Sqrt(-2.0 * Math.Log(u1)) * (float)Math.Cos(2.0 * Math.PI * u2);
        }
        return noise;
    }

    private static LipSynthesisResult Skipped(LipSynthesisRequest request, string reason) =>
        new(request.SegmentId, LipSynthesisEngineStatus.Skipped,
            PatchedClipPath: null, SkipReason: reason,
            FailureReason: null, ProviderId: "latentsync-onnx", ModelId: LatentSyncModelPaths.ModelId);

    private static LipSynthesisResult Failed(LipSynthesisRequest request, string reason) =>
        new(request.SegmentId, LipSynthesisEngineStatus.Failed,
            PatchedClipPath: null, SkipReason: null,
            FailureReason: reason, ProviderId: "latentsync-onnx", ModelId: LatentSyncModelPaths.ModelId);

    internal static bool IsExperimentalFromManifest(BundledModelManifestRegistry registry)
    {
        if (!registry.TryResolve(LatentSyncModelPaths.ManifestAlias, out BundledModelManifestResolution? resolution) ||
            resolution is null)
        {
            return true;
        }

        return IsExperimentalFromEntry(resolution.Entry);
    }

    internal static bool IsExperimentalFromEntry(BundledModelManifestEntry entry) =>
        entry.Lane != ModelLane.Commercial
        || !entry.CommercialUseVerified
        || !entry.CommercialAllowed;
}
