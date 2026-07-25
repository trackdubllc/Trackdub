using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.CosyVoice;

/// <summary>
/// CosyVoice voice-cloning TTS adapter for the bundled
/// <c>tonythethompson/CosyVoice-300M-ONNX</c> multi-graph ONNX bundle.
/// </summary>
public sealed class CosyVoiceTtsEngine(
    IConsentService consentService,
    BenchmarkModelPathResolver modelPathResolver)
    : ITtsEngineAdapter, IStageRuntimeExecutionReporter, IDisposable
{
    public const string EngineFamilyName = "cosyvoice";

    private readonly IConsentService consentService = consentService ?? throw new ArgumentNullException(nameof(consentService));
    private readonly BenchmarkModelPathResolver modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private PinnedRuntime? pinnedRuntime;
    private int disposeSignaled;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public string EngineFamily => EngineFamilyName;

    public Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("CosyVoice voice cloning must be invoked through the runtime-planned overload.");
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.VoiceCloneReference is null)
        {
            throw new InvalidOperationException("CosyVoice voice cloning requires a reference clip.");
        }

        if (!consentService.IsVoiceCloningConsentGranted)
        {
            throw new ConsentRequiredException();
        }

        if (string.IsNullOrWhiteSpace(request.VoiceCloneReference.ReferenceTranscript))
        {
            throw new TtsReferenceTextRequiredException();
        }

        CosyVoiceReferenceValidator.Validate(request.VoiceCloneReference.ReferenceClipPath);
        EnsurePlanReady(plan);

        string modelRootPath = PlannedRuntimeModelResolver.ResolveModelRootPath(plan, modelPathResolver);
        CosyVoiceModelFiles modelFiles = CosyVoiceModelFiles.Resolve(modelRootPath, plan.Variant);
        IReadOnlyList<string> missingFiles = modelFiles.FindMissingFiles();
        if (missingFiles.Count > 0)
        {
            throw new InvalidOperationException(
                "CosyVoice native runtime bundle is incomplete. " +
                $"Missing: {string.Join(", ", missingFiles)}");
        }

        ThrowIfDisposed();
        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            PinnedRuntime runtime = await GetOrCreatePinnedRuntimeAsync(
                modelFiles,
                plan.ExecutionProvider!.Value,
                cancellationToken).ConfigureAwait(false);

            float[] audioSamples = runtime.Pipeline.Synthesize(
                request.Text,
                request.VoiceCloneReference.ReferenceTranscript.Trim(),
                request.VoiceCloneReference.ReferenceClipPath,
                cancellationToken);

            byte[] wavBytes = WaveAudioWriter.EncodeMonoPcm16(audioSamples, CosyVoiceConstants.SampleRate);
            LastExecutionSummary = new StageRuntimeExecutionSummary(
                runtime.Sessions.TextEncoder.RequestedProvider,
                runtime.Sessions.SelectedProvider,
                plan.ModelId,
                plan.ModelAlias,
                plan.Variant,
                runtime.Sessions.TextEncoder.BootstrapDetail);

            return new TtsSynthesisResult(
                wavBytes,
                audioSamples.Length,
                CosyVoiceConstants.SampleRate,
                plan.ModelId ?? "tonythethompson/CosyVoice-300M-ONNX",
                request.Voice.VoiceId,
                runtime.Sessions.SelectedProvider);
        }
        finally
        {
            sessionGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeSignaled, 1) != 0)
        {
            return;
        }

        sessionGate.Wait();
        try
        {
            pinnedRuntime?.Dispose();
            pinnedRuntime = null;
        }
        finally
        {
            sessionGate.Release();
            sessionGate.Dispose();
        }
    }

    private async Task<PinnedRuntime> GetOrCreatePinnedRuntimeAsync(
        CosyVoiceModelFiles modelFiles,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        if (pinnedRuntime is not null &&
            pinnedRuntime.Matches(modelFiles.ModelRootPath, modelFiles.Variant, provider))
        {
            return pinnedRuntime;
        }

        pinnedRuntime?.Dispose();
        CosyVoiceOnnxSessions sessions = await CosyVoiceOnnxSessions.CreateAsync(
            modelFiles,
            provider,
            cancellationToken).ConfigureAwait(false);
        CosyVoiceEmbeddingTables embeddings = CosyVoiceEmbeddingTables.Load(modelFiles.ModelRootPath);
        CosyVoiceWhisperTokenizer tokenizer = CosyVoiceWhisperTokenizer.Load(modelFiles.ModelRootPath);
        var pipeline = new CosyVoiceSynthesisPipeline(sessions, embeddings, tokenizer);
        pinnedRuntime = new PinnedRuntime(modelFiles.ModelRootPath, modelFiles.Variant, provider, sessions, pipeline);
        return pinnedRuntime;
    }

    private static void EnsurePlanReady(StageRuntimePlan plan)
    {
        if (plan.ExecutionProvider is null)
        {
            throw new InvalidOperationException("Execution provider not resolved in runtime plan.");
        }

        if (plan.Status is not (StageRuntimePlanStatus.Ready or StageRuntimePlanStatus.Verified))
        {
            throw new InvalidOperationException($"CosyVoice runtime plan is not ready: {plan.Status}.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposeSignaled != 0)
        {
            throw new ObjectDisposedException(nameof(CosyVoiceTtsEngine));
        }
    }

    private sealed class PinnedRuntime : IDisposable
    {
        public PinnedRuntime(
            string modelRootPath,
            string variant,
            ExecutionProviderKind provider,
            CosyVoiceOnnxSessions sessions,
            CosyVoiceSynthesisPipeline pipeline)
        {
            ModelRootPath = modelRootPath;
            Variant = variant;
            Provider = provider;
            Sessions = sessions;
            Pipeline = pipeline;
        }

        public string ModelRootPath { get; }

        public string Variant { get; }

        public ExecutionProviderKind Provider { get; }

        public CosyVoiceOnnxSessions Sessions { get; }

        public CosyVoiceSynthesisPipeline Pipeline { get; }

        public bool Matches(string modelRootPath, string variant, ExecutionProviderKind provider) =>
            string.Equals(ModelRootPath, modelRootPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Variant, variant, StringComparison.OrdinalIgnoreCase) &&
            Provider == provider;

        public void Dispose() => Sessions.Dispose();
    }
}
