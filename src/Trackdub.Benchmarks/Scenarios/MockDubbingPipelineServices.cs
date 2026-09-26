using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Application.Dubbing;
using Trackdub.Application.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.Pipeline;

namespace Trackdub.Benchmarks.Scenarios;

/// <summary>
/// Audio preparation pipeline stage contract for benchmarking test doubles.
/// </summary>
public interface IAudioPreparationStage
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Speech separation pipeline stage contract for benchmarking test doubles.
/// </summary>
public interface ISpeechSeparationStage
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Transcription (ASR) pipeline stage contract for benchmarking test doubles.
/// </summary>
public interface ITranscriptionStage
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Lip-sync and forced alignment pipeline stage contract for benchmarking test doubles.
/// </summary>
public interface ILipSyncStage
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Text-to-speech and dubbing synthesis pipeline stage contract for benchmarking test doubles.
/// </summary>
public interface ITtsStage
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Mock dubbing pipeline service interface orchestrating canonical mock stages.
/// </summary>
public interface IDubbingPipelineService
{
    /// <summary>
    /// Executes the mock dubbing pipeline deterministically.
    /// </summary>
    Task<DubbingRunResult> ExecuteAsync(
        DubbingSessionOptions options,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns simulated stage run records for provenance and report mapping.
    /// </summary>
    IReadOnlyList<StageRunRecord> GetStageRuns(string project, string? requestedProvider, string? requestedModel);
}

/// <summary>
/// Configuration parameters for deterministic mock pipeline execution.
/// </summary>
public sealed class MockPipelineOptions
{
    public bool DryRun { get; set; }

    public Dictionary<string, TimeSpan> SimulatedStageLatencies { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio-prep"] = TimeSpan.FromMilliseconds(10),
        ["audio-preparation"] = TimeSpan.FromMilliseconds(10),
        ["separation"] = TimeSpan.FromMilliseconds(20),
        ["transcription"] = TimeSpan.FromMilliseconds(30),
        ["asr"] = TimeSpan.FromMilliseconds(30),
        ["alignment"] = TimeSpan.FromMilliseconds(15),
        ["lip-sync"] = TimeSpan.FromMilliseconds(15),
        ["dubbing"] = TimeSpan.FromMilliseconds(25),
        ["tts"] = TimeSpan.FromMilliseconds(25),
    };

    public Dictionary<string, long> SimulatedMemoryAllocations { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio-prep"] = 5 * 1024 * 1024,
        ["separation"] = 15 * 1024 * 1024,
        ["transcription"] = 25 * 1024 * 1024,
        ["alignment"] = 10 * 1024 * 1024,
        ["dubbing"] = 20 * 1024 * 1024,
    };

    public string? FailStage { get; set; }

    public string? FailureReason { get; set; }

    public string DefaultProvider { get; set; } = "cpu";

    public string DefaultModel { get; set; } = "mock-model";

    public TimeSpan GetSimulatedLatency(string stageName)
    {
        if (DryRun)
        {
            return TimeSpan.Zero;
        }

        if (SimulatedStageLatencies.TryGetValue(stageName, out TimeSpan latency))
        {
            return latency;
        }

        string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stageName);
        if (SimulatedStageLatencies.TryGetValue(canonical, out latency))
        {
            return latency;
        }

        return TimeSpan.FromMilliseconds(5);
    }

    public long GetSimulatedAllocation(string stageName)
    {
        if (SimulatedMemoryAllocations.TryGetValue(stageName, out long bytes))
        {
            return bytes;
        }

        string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stageName);
        if (SimulatedMemoryAllocations.TryGetValue(canonical, out bytes))
        {
            return bytes;
        }

        return 1024 * 1024;
    }
}

/// <summary>
/// Mock audio preparation stage implementation.
/// </summary>
public sealed class MockAudioPreparationStage(MockPipelineOptions options) : IAudioPreparationStage
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = options.GetSimulatedLatency("audio-prep");
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        long bytes = options.GetSimulatedAllocation("audio-prep");
        if (bytes > 0)
        {
            byte[] buffer = new byte[Math.Min(bytes, 1024 * 1024)];
            GC.KeepAlive(buffer);
        }
    }
}

/// <summary>
/// Mock speech separation stage implementation.
/// </summary>
public sealed class MockSpeechSeparationStage(MockPipelineOptions options) : ISpeechSeparationStage
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = options.GetSimulatedLatency("separation");
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        long bytes = options.GetSimulatedAllocation("separation");
        if (bytes > 0)
        {
            byte[] buffer = new byte[Math.Min(bytes, 1024 * 1024)];
            GC.KeepAlive(buffer);
        }
    }
}

/// <summary>
/// Mock transcription stage implementation.
/// </summary>
public sealed class MockTranscriptionStage(MockPipelineOptions options) : ITranscriptionStage
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = options.GetSimulatedLatency("transcription");
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        long bytes = options.GetSimulatedAllocation("transcription");
        if (bytes > 0)
        {
            byte[] buffer = new byte[Math.Min(bytes, 1024 * 1024)];
            GC.KeepAlive(buffer);
        }
    }
}

/// <summary>
/// Mock lip-sync stage implementation.
/// </summary>
public sealed class MockLipSyncStage(MockPipelineOptions options) : ILipSyncStage
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = options.GetSimulatedLatency("alignment");
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        long bytes = options.GetSimulatedAllocation("alignment");
        if (bytes > 0)
        {
            byte[] buffer = new byte[Math.Min(bytes, 1024 * 1024)];
            GC.KeepAlive(buffer);
        }
    }
}

/// <summary>
/// Mock TTS / dubbing stage implementation.
/// </summary>
public sealed class MockTtsStage(MockPipelineOptions options) : ITtsStage
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = options.GetSimulatedLatency("dubbing");
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        long bytes = options.GetSimulatedAllocation("dubbing");
        if (bytes > 0)
        {
            byte[] buffer = new byte[Math.Min(bytes, 1024 * 1024)];
            GC.KeepAlive(buffer);
        }
    }
}

/// <summary>
/// Deterministic mock dubbing pipeline service implementation.
/// </summary>
public sealed class MockDubbingPipelineService(
    MockPipelineOptions options,
    IAudioPreparationStage audioPrepStage,
    ISpeechSeparationStage separationStage,
    ITranscriptionStage transcriptionStage,
    ILipSyncStage lipSyncStage,
    ITtsStage ttsStage) : IDubbingPipelineService
{
    private static readonly string[] CanonicalStages =
    [
        "audio-prep",
        "separation",
        "transcription",
        "alignment",
        "dubbing"
    ];

    private readonly ConcurrentDictionary<string, List<string>> _projectExecutedStages = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DubbingRunResult> ExecuteAsync(
        DubbingSessionOptions sessionOptions,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(sessionOptions.ProjectOutputDirectory))
        {
            Directory.CreateDirectory(sessionOptions.ProjectOutputDirectory);
        }

        Guid runId = Guid.NewGuid();
        DateTimeOffset runStart = DateTimeOffset.UtcNow;
        var outcomes = new List<StageOutcome>();
        var canonicalStagesRun = new List<string>();

        IReadOnlyList<string> stagesToRun = sessionOptions.StageFilter is { Count: > 0 }
            ? sessionOptions.StageFilter
            : CanonicalStages;

        foreach (string stageName in stagesToRun)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stageName);
            canonicalStagesRun.Add(canonical);
            if (!canonical.Equals(stageName, StringComparison.OrdinalIgnoreCase))
            {
                canonicalStagesRun.Add(stageName);
            }
            string outcomeStageName = sessionOptions.StageFilter is { Count: > 0 } ? stageName : canonical;
            DateTimeOffset stageStart = DateTimeOffset.UtcNow;

            progress?.Report(new PipelineProgressEvent(
                StageName: canonical,
                EventKind: PipelineProgressEventKind.Started,
                Message: $"Starting {canonical}"));

            bool shouldFail = !string.IsNullOrWhiteSpace(options.FailStage) &&
                (string.Equals(options.FailStage, stageName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(options.FailStage, canonical, StringComparison.OrdinalIgnoreCase));

            if (shouldFail)
            {
                string reason = options.FailureReason ?? $"Simulated failure in stage '{canonical}'.";
                DateTimeOffset stageFailTime = DateTimeOffset.UtcNow;
                progress?.Report(new PipelineProgressEvent(
                    StageName: canonical,
                    EventKind: PipelineProgressEventKind.Failed,
                    Message: reason,
                    ElapsedDuration: stageFailTime - stageStart));

                outcomes.Add(new StageOutcome
                {
                    StageName = outcomeStageName,
                    Status = StageStatus.Failed,
                    StartTime = stageStart,
                    EndTime = stageFailTime,
                    ReasonCode = reason,
                    ArtifactPaths = [],
                });

                if (!string.IsNullOrWhiteSpace(sessionOptions.ProjectOutputDirectory))
                {
                    _projectExecutedStages.AddOrUpdate(
                        sessionOptions.ProjectOutputDirectory,
                        canonicalStagesRun,
                        (_, existing) =>
                        {
                            var combined = new List<string>(existing);
                            foreach (string s in canonicalStagesRun.Where(
                                s => !combined.Contains(s, StringComparer.OrdinalIgnoreCase)))
                            {
                                combined.Add(s);
                            }
                            return combined;
                        });
                }

                return new DubbingRunResult
                {
                    RunId = runId,
                    CorrelationId = runId,
                    StartTime = runStart,
                    EndTime = stageFailTime,
                    OverallStatus = DubbingRunStatus.Failed,
                    StageOutcomes = outcomes.AsReadOnly(),
                    ExecutionSnapshot = new Dictionary<string, string>(),
                    PreFlightFailures = [reason]
                };
            }

            try
            {
                switch (canonical)
                {
                    case "audio-prep":
                        await audioPrepStage.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "separation":
                        await separationStage.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "transcription":
                        await transcriptionStage.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "alignment":
                        await lipSyncStage.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "dubbing":
                        await ttsStage.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        TimeSpan defaultDelay = options.GetSimulatedLatency(stageName);
                        if (defaultDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(defaultDelay, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                DateTimeOffset cancelTime = DateTimeOffset.UtcNow;
                progress?.Report(new PipelineProgressEvent(
                    StageName: canonical,
                    EventKind: PipelineProgressEventKind.Failed,
                    Message: "Canceled",
                    ElapsedDuration: cancelTime - stageStart));
                throw;
            }

            DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
            progress?.Report(new PipelineProgressEvent(
                StageName: canonical,
                EventKind: PipelineProgressEventKind.Completed,
                Message: $"Completed {canonical}",
                ElapsedDuration: stageEnd - stageStart));


            outcomes.Add(new StageOutcome
            {
                StageName = outcomeStageName,
                Status = StageStatus.Succeeded,
                StartTime = stageStart,
                EndTime = stageEnd,
                ArtifactPaths = [],
            });
        }

        if (!string.IsNullOrWhiteSpace(sessionOptions.ProjectOutputDirectory))
        {
            _projectExecutedStages.AddOrUpdate(
                sessionOptions.ProjectOutputDirectory,
                canonicalStagesRun,
                (_, existing) =>
                {
                    var combined = new List<string>(existing);
                    foreach (string s in canonicalStagesRun.Where(
                        s => !combined.Contains(s, StringComparer.OrdinalIgnoreCase)))
                    {
                        combined.Add(s);
                    }
                    return combined;
                });
        }

        return new DubbingRunResult
        {
            RunId = runId,
            CorrelationId = runId,
            StartTime = runStart,
            EndTime = DateTimeOffset.UtcNow,
            OverallStatus = DubbingRunStatus.Succeeded,
            StageOutcomes = outcomes.AsReadOnly(),
            ExecutionSnapshot = new Dictionary<string, string>(),
        };
    }

    public IReadOnlyList<StageRunRecord> GetStageRuns(string project, string? requestedProvider, string? requestedModel)
    {
        string provider = requestedProvider ?? options.DefaultProvider;
        string model = requestedModel ?? options.DefaultModel;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<string> stages = _projectExecutedStages.TryGetValue(project, out var list) && list.Count > 0
            ? list
            : Array.Empty<string>();

        var records = new List<StageRunRecord>(stages.Count);
        foreach (string stage in stages)
        {
            records.Add(new StageRunRecord(
                Id: Guid.NewGuid(),
                ProjectId: Guid.NewGuid(),
                StageName: stage,
                Status: StageRunStatus.Completed,
                StartedAtUtc: now,
                CompletedAtUtc: now,
                FailureReason: null,
                RuntimeInfo: new StageRunRuntimeInfo(
                    requestedProvider: provider,
                    selectedProvider: provider,
                    modelId: model,
                    modelAlias: model)));
        }

        return records;
    }
}

/// <summary>
/// Pre-flight checker test double for mock benchmark execution.
/// </summary>
public sealed class MockPipelinePreFlightChecker : IPipelinePreFlightChecker
{
    public Task EnsureModelsAvailableAsync(
        string stageName,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Media probe test double returning synthetic audio media dimensions.
/// </summary>
public sealed class MockMediaProbe : IMediaProbe
{
    public Task<MediaProbeSnapshot> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new MediaProbeSnapshot(
            FormatName: "wav",
            FormatLongName: "WAV / WAVE (Waveform Audio)",
            DurationSeconds: 1.0,
            BitRate: 256000,
            AudioStreams: [new MediaAudioStream(0, "pcm_s16le", 1, 16000, 1.0)],
            VideoStreams: []);
        return Task.FromResult(snapshot);
    }
}

/// <summary>
/// Helper for registering mock dubbing pipeline services in Microsoft.Extensions.DependencyInjection.
/// </summary>
public static class MockDubbingPipelineServices
{
    /// <summary>
    /// Normalizes a pipeline stage name to its canonical benchmark alias.
    /// </summary>
    public static string CanonicalBenchmarkStage(string stageName) => stageName.ToLowerInvariant() switch
    {
        "audio-preparation" or "audio-prep" or "audiopreparation" => "audio-prep",
        "separation" or "stemseparation" or "stem-separation" => "separation",
        "transcription" or "asr" => "transcription",
        "alignment" or "lipsync" or "lip-sync" => "alignment",
        "dubbing" or "tts" => "dubbing",
        _ => stageName
    };

    /// <summary>
    /// Configures the service collection to use deterministic mock doubles for all canonical pipeline stages.
    /// </summary>
    public static void ConfigureMockPipeline(IServiceCollection services, Action<MockPipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MockPipelineOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.Replace(ServiceDescriptor.Singleton(options));

        services.TryAddSingleton<IAudioPreparationStage, MockAudioPreparationStage>();
        services.Replace(ServiceDescriptor.Singleton<IAudioPreparationStage, MockAudioPreparationStage>());

        services.TryAddSingleton<ISpeechSeparationStage, MockSpeechSeparationStage>();
        services.Replace(ServiceDescriptor.Singleton<ISpeechSeparationStage, MockSpeechSeparationStage>());

        services.TryAddSingleton<ITranscriptionStage, MockTranscriptionStage>();
        services.Replace(ServiceDescriptor.Singleton<ITranscriptionStage, MockTranscriptionStage>());

        services.TryAddSingleton<ILipSyncStage, MockLipSyncStage>();
        services.Replace(ServiceDescriptor.Singleton<ILipSyncStage, MockLipSyncStage>());

        services.TryAddSingleton<ITtsStage, MockTtsStage>();
        services.Replace(ServiceDescriptor.Singleton<ITtsStage, MockTtsStage>());

        services.TryAddSingleton<IDubbingPipelineService, MockDubbingPipelineService>();
        services.Replace(ServiceDescriptor.Singleton<IDubbingPipelineService, MockDubbingPipelineService>());

        services.RemoveAll<IPipelineReadinessService>();
        services.Replace(ServiceDescriptor.Singleton<IPipelinePreFlightChecker, MockPipelinePreFlightChecker>());
        services.Replace(ServiceDescriptor.Singleton<IMediaProbe, MockMediaProbe>());
    }

    /// <summary>
    /// Returns a service configurator action that applies mock pipeline configuration.
    /// </summary>
    public static Action<IServiceCollection> Configure(Action<MockPipelineOptions>? configure = null) =>
        services => ConfigureMockPipeline(services, configure);
}
