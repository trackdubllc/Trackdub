using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Kokoro;

public sealed class KokoroTtsEngine : ITtsEngineAdapter, IStageRuntimeExecutionReporter
{
    public const string EngineFamilyName = "kokoro";

    private const string ModelAlias = "kokoro-onnx";
    private const int SampleRate = 24_000;

    private readonly IRuntimePlanner runtimePlanner;
    private readonly IRuntimePlanningPreferences? runtimePlanningPreferences;
    private readonly BenchmarkModelPathResolver modelPathResolver;
    private readonly IGraphemeToPhoneme phonemizer;
    private readonly SidecarCache<KokoroTokenizer> tokenizerCache;
    private readonly SidecarCache<KokoroVoiceCatalog> voiceCatalogCache;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private PinnedSession? pinnedSession;
    private int disposed;

    /// <summary>
    /// Creates a <see cref="KokoroTtsEngine"/> that uses the process-wide shared sidecar caches.
    /// </summary>
    /// <param name="runtimePlanner">Planner that selects the model/provider at runtime.</param>
    /// <param name="modelPathResolver">Resolver for bundled model file paths.</param>
    /// <param name="phonemizer">Grapheme-to-phoneme converter.</param>
    public KokoroTtsEngine(
        IRuntimePlanner runtimePlanner,
        BenchmarkModelPathResolver modelPathResolver,
        IGraphemeToPhoneme phonemizer)
        : this(runtimePlanner, modelPathResolver, phonemizer, null, null, null)
    {
    }

    /// <summary>
    /// Creates a <see cref="KokoroTtsEngine"/> with explicit sidecar caches.
    /// This overload is intended for tests that need isolated (non-shared) caches.
    /// </summary>
    /// <param name="runtimePlanner">Planner that selects the model/provider at runtime.</param>
    /// <param name="modelPathResolver">Resolver for bundled model file paths.</param>
    /// <param name="phonemizer">Grapheme-to-phoneme converter.</param>
    /// <param name="tokenizerCache">
    /// Shared cache for <see cref="KokoroTokenizer"/> instances keyed by model-root path.
    /// The tokenizer is loaded at most once per unique model root.
    /// Pass <see langword="null"/> to fall back to the process-wide <see cref="KokoroSidecarCaches.Tokenizers"/> instance.
    /// </param>
    /// <param name="voiceCatalogCache">
    /// Shared cache for <see cref="KokoroVoiceCatalog"/> instances keyed by model-root path.
    /// Pass <see langword="null"/> to fall back to the process-wide <see cref="KokoroSidecarCaches.VoiceCatalogs"/> instance.
    /// </param>
    internal KokoroTtsEngine(
        IRuntimePlanner runtimePlanner,
        BenchmarkModelPathResolver modelPathResolver,
        IGraphemeToPhoneme phonemizer,
        SidecarCache<KokoroTokenizer>? tokenizerCache,
        SidecarCache<KokoroVoiceCatalog>? voiceCatalogCache,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    {
        this.runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
        this.runtimePlanningPreferences = runtimePlanningPreferences;
        this.modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));
        this.phonemizer = phonemizer ?? throw new ArgumentNullException(nameof(phonemizer));
        this.tokenizerCache = tokenizerCache ?? KokoroSidecarCaches.Tokenizers;
        this.voiceCatalogCache = voiceCatalogCache ?? KokoroSidecarCaches.VoiceCatalogs;
    }

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public string EngineFamily => EngineFamilyName;

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InferenceRequestOptions options = request.Options ?? InferenceRequestOptions.Default;

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(
            await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(new StageRuntimePlanningRequest(
                RuntimeStage.Tts,
                options.NormalizedPreferredModelAlias ?? ModelAlias,
                SourceLanguage: request.LanguageCode,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    options.PreferredExecutionProvider,
                    options.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: options.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: options.NormalizedPreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return await SynthesizeAsync(request, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        EnsurePlanReady(plan);

        BenchmarkModelCandidate candidate = PlannedRuntimeModelResolver.ResolveCandidate(plan, modelPathResolver);
        // Prefer the manifest-declared model root (which contains tokenizer.json and voices/).
        // Fall back to the model file's parent directory only when the resolver couldn't
        // determine a root (e.g. user supplied a bare .onnx path outside the bundled layout).
        string modelRootPath = candidate.RootDirectory
            ?? Path.GetDirectoryName(candidate.ModelPath)
            ?? throw new InvalidOperationException("Cannot resolve Kokoro model root path.");

        // Perform phonemization before acquiring the gate so concurrent requests can
        // overlap this CPU-only work with inference from a prior call. Tokenization and
        // voice/style resolution remain inside the gated section because they use session state.
        string phonemes = string.IsNullOrWhiteSpace(request.PhonemeOverride)
            ? phonemizer.Phonemize(request.Text, request.LanguageCode)
            : request.PhonemeOverride;

        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            PinnedSession session = await GetOrCreatePinnedSessionAsync(
                candidate.ModelPath,
                modelRootPath,
                plan.ExecutionProvider!.Value,
                cancellationToken).ConfigureAwait(false);

            long[] inputIds = session.Tokenizer.Encode(phonemes);

            string binPath = session.VoiceCatalog.GetBinPath(request.Voice.VoiceId)
                ?? throw new FileNotFoundException(
                    $"Voicepack '{request.Voice.VoiceId}' not found under '{modelRootPath}/voices/'.",
                    request.Voice.VoiceId);

            // Upstream Kokoro indexes the style matrix by the raw phoneme count (pre-padding),
            // i.e. ref_s = voices[len(phoneme_tokens)]. Our `inputIds` wraps BOS/EOS, so subtract 2.
            int phonemeTokenCount = Math.Max(0, inputIds.Length - 2);
            float[] styleVector = KokoroVoicepackLoader.LoadStyleVector(binPath, phonemeTokenCount);

            float[] audioSamples = RunInference(session.Lease.Session, inputIds, styleVector, request.Speed);
            byte[] wavBytes = KokoroPcmConverter.EncodePcm16Wav(audioSamples, SampleRate);

            LastExecutionSummary = new StageRuntimeExecutionSummary(
                session.Lease.RequestedProvider,
                session.Lease.SelectedProvider,
                plan.ModelId,
                plan.ModelAlias,
                plan.Variant,
                session.Lease.BootstrapDetail);

            return new TtsSynthesisResult(
                wavBytes,
                DurationSamples: audioSamples.Length,
                SampleRate: SampleRate,
                ModelId: plan.ModelId ?? ModelAlias,
                VoiceId: request.Voice.VoiceId,
                Provider: session.Lease.SelectedProvider);
        }
        finally
        {
            sessionGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        if (!sessionGate.Wait(TimeSpan.FromSeconds(10)))
        {
            // Timeout — a concurrent async operation is still using the engine.
            // Proceed with disposal anyway; the concurrent operation will hit
            // ObjectDisposedException on the disposed gate/session.
            sessionGate.Dispose();
            return;
        }
        try
        {
            pinnedSession?.Lease.Dispose();
            pinnedSession = null;
        }
        finally
        {
            // Dispose without releasing: any concurrent WaitAsync gets ObjectDisposedException
            // rather than entering the try block on a partially-disposed engine.
            sessionGate.Dispose();
        }
    }

    private async Task<PinnedSession> GetOrCreatePinnedSessionAsync(
        string modelPath,
        string modelRootPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        if (pinnedSession is not null &&
            string.Equals(pinnedSession.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pinnedSession.ModelRootPath, modelRootPath, StringComparison.OrdinalIgnoreCase) &&
            pinnedSession.Provider == provider)
        {
            return pinnedSession;
        }

        pinnedSession?.Lease.Dispose();
        pinnedSession = null;
        OnnxExecutionSessionFactory.SingleSessionLease lease = await OnnxExecutionSessionFactory
            .CreatePooledSingleAsync("kokoro", modelPath, provider, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // Load tokenizer and voice catalog from the shared sidecar caches so they are not
            // re-read from disk when a new engine instance is created for the same model root
            // (e.g. across DI scope boundaries).  Both are pure-read, immutable once loaded.
            KokoroTokenizer tokenizer = await tokenizerCache.GetOrAddAsync(
                modelRootPath,
                async key => await KokoroTokenizer.LoadAsync(key).ConfigureAwait(false)).ConfigureAwait(false);
            KokoroVoiceCatalog voiceCatalog = await voiceCatalogCache.GetOrAddAsync(
                modelRootPath,
                async key => await KokoroVoiceCatalog.LoadAsync(key).ConfigureAwait(false)).ConfigureAwait(false);
            pinnedSession = new PinnedSession(modelPath, modelRootPath, provider, lease, tokenizer, voiceCatalog);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        return pinnedSession;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private static float[] RunInference(
        InferenceSession session,
        long[] inputIds,
        float[] styleVector,
        float speed)
    {
        var inputs = new List<NamedOnnxValue>(3);
        foreach ((string name, _) in session.InputMetadata)
        {
            inputs.Add(name switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(
                    "input_ids",
                    new DenseTensor<long>(inputIds, [1, inputIds.Length])),
                "style" => NamedOnnxValue.CreateFromTensor(
                    "style",
                    new DenseTensor<float>(styleVector, [1, 256])),
                "speed" => NamedOnnxValue.CreateFromTensor(
                    "speed",
                    new DenseTensor<float>(new[] { speed }, new[] { 1 })),
                _ => throw new NotSupportedException($"Kokoro input '{name}' is not supported.")
            });
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.RunWithRetry(inputs);

        // Model has one output (waveform/audio); take the first regardless of name.
        DisposableNamedOnnxValue audioOutput = results.Count == 1
            ? results.Single()
            : results.FirstOrDefault(static r => r.Name is "audio" or "waveform")
              ?? throw new InvalidOperationException(
                  $"Kokoro output not found. Available: {string.Join(", ", results.Select(static r => r.Name))}");

        Tensor<float> audioTensor = audioOutput.AsTensor<float>();
        return audioTensor.ToArray();
    }

    private sealed class PinnedSession(
        string modelPath,
        string modelRootPath,
        ExecutionProviderKind provider,
        OnnxExecutionSessionFactory.SingleSessionLease lease,
        KokoroTokenizer tokenizer,
        KokoroVoiceCatalog voiceCatalog)
        : IDisposable
    {
        public string ModelPath { get; } = modelPath;
        public string ModelRootPath { get; } = modelRootPath;
        public ExecutionProviderKind Provider { get; } = provider;
        public OnnxExecutionSessionFactory.SingleSessionLease Lease { get; } = lease;
        public KokoroTokenizer Tokenizer { get; } = tokenizer;
        public KokoroVoiceCatalog VoiceCatalog { get; } = voiceCatalog;

        public void Dispose()
        {
            Lease.Dispose();
        }
    }

    private static void EnsurePlanReady(StageRuntimePlan plan)
    {
        if (plan.IsRunnable() &&
            plan.ExecutionProvider is not null &&
            !string.IsNullOrWhiteSpace(plan.ModelAlias))
        {
            return;
        }

        throw new InvalidOperationException(
            plan.Fallback?.Detail ?? "Runtime planner did not produce a ready TTS plan.");
    }

}
