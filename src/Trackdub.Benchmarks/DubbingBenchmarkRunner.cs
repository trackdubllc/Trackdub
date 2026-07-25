using System.Security.Cryptography;
using System.Text;
using Trackdub.Application.Dubbing;
using Trackdub.Composition.Headless;
using Trackdub.Contracts.Dubbing;

namespace Trackdub.Benchmarks;

/// <summary>
/// Runs the real Trackdub dubbing pipeline against a media file and records
/// per-stage wall-clock timings from <see cref="DubbingRunResult.StageOutcomes"/>.
/// Bootstraps via <see cref="HeadlessDubbingHost"/> (Composition), not Trackdub.Sdk.
/// </summary>
public sealed class DubbingBenchmarkRunner
{
    private readonly string? _modelDirectory;
    private readonly string? _ffmpegPath;
    private readonly string? _ffprobePath;

    /// <summary>
    /// Creates a new runner that uses default model/ffmpeg discovery.
    /// </summary>
    public DubbingBenchmarkRunner(string? modelDirectory = null, string? ffmpegPath = null, string? ffprobePath = null)
    {
        _modelDirectory = modelDirectory;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
    }

    /// <summary>
    /// Builds a headless host with this runner's model/ffmpeg configuration.
    /// Caller owns disposal. Used by batch mode to share one host across runs.
    /// </summary>
    public HeadlessDubbingHost CreateHost() => HeadlessDubbingHost.Create(BuildOptions());

    /// <summary>
    /// Executes the full dubbing pipeline for the given options and returns a
    /// <see cref="DubbingBenchmarkReport"/> with per-stage timings.
    /// Creates and disposes its own headless host.
    /// </summary>
    public async Task<DubbingBenchmarkReport> RunAsync(
        DubbingBenchmarkOptions options,
        CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        string hardwareInfo = BenchmarkHardwareInfo.Capture();

        HeadlessDubbingHost host;
        try
        {
            host = CreateHost();
        }
        catch (Exception ex)
        {
            // Host creation failed - return failed report with error message
            return FailedReport(options, hardwareInfo, startedAtUtc, ex.Message);
        }

        try
        {
            return await RunAsync(host, options, hardwareInfo, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// Executes one dubbing run using a pre-built headless host (batch mode).
    /// Does not dispose <paramref name="host"/>.
    /// </summary>
    public async Task<DubbingBenchmarkReport> RunAsync(
        HeadlessDubbingHost host,
        DubbingBenchmarkOptions options,
        string hardwareInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareInfo);

        var startedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            string projectOutputDirectory = DeriveProjectDirectory(
                options.InputPath,
                options.OutputDirectory,
                options.TargetLanguage);

            DubbingPipelineEngine engine = host.CreateEngine();

            var pipelineOptions = new DubbingSessionOptions
            {
                SourceMediaPath = options.InputPath,
                ProjectOutputDirectory = projectOutputDirectory,
                SourceLanguageCode = options.SourceLanguageCode,
                TargetLanguageCode = options.TargetLanguage,
                ForceRerun = options.ForceRerun,
            };

            DubbingRunResult result = await engine.ExecuteAsync(
                pipelineOptions,
                progress: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            int segmentCount = await TryReadSegmentCountAsync(
                host.SessionFactory,
                projectOutputDirectory,
                options.SourceLanguageCode,
                options.TargetLanguage,
                cancellationToken).ConfigureAwait(false);

            return BuildReportFromResult(
                options,
                result,
                hardwareInfo,
                startedAtUtc,
                segmentCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailedReport(options, hardwareInfo, startedAtUtc, ex.Message);
        }
    }

    private HeadlessTrackdubOptions BuildOptions() =>
        new()
        {
            ModelDirectory = _modelDirectory,
            FfmpegPath = _ffmpegPath,
            FfprobePath = _ffprobePath,
        };

    private static string DeriveProjectDirectory(
        string inputPath,
        string? outputDirectory,
        string targetLanguage)
    {
        string baseDir = outputDirectory
            ?? Path.GetDirectoryName(inputPath)
            ?? ".";

        string pathHash = ComputePathHash(inputPath);
        string baseName = Path.GetFileNameWithoutExtension(inputPath);

        return Path.Combine(
            baseDir,
            $"{baseName}-{pathHash}-{targetLanguage}.trackdub");
    }

    /// <summary>
    /// Computes a short stable hash of the normalized path to distinguish inputs
    /// with the same filename but different locations.
    /// </summary>
    public static string ComputePathHash(string path)
    {
        string normalized = Path.GetFullPath(path).ToLowerInvariant();
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static async Task<int> TryReadSegmentCountAsync(
        IDubbingSessionFactory factory,
        string projectOutputDirectory,
        string? sourceLanguageCode,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = Trackdub.Contracts.StudioSettings.Default with
            {
                DefaultSourceLanguage = sourceLanguageCode,
                DefaultTargetLanguage = targetLanguage,
            };

            await using IDubbingSession session = factory.CreateSession(projectOutputDirectory, settings);
            var state = await session.Workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
            return state.TranscriptSegments.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    private static DubbingBenchmarkReport BuildReportFromResult(
        DubbingBenchmarkOptions options,
        DubbingRunResult result,
        string hardwareInfo,
        DateTimeOffset startedAtUtc,
        int segmentCount)
    {
        TimeSpan asr = TimeSpan.Zero;
        TimeSpan translation = TimeSpan.Zero;
        TimeSpan tts = TimeSpan.Zero;
        TimeSpan mixing = TimeSpan.Zero;

        foreach (StageOutcome outcome in result.StageOutcomes)
        {
            TimeSpan elapsed = outcome.EndTime - outcome.StartTime;

            switch (outcome.StageName.ToUpperInvariant())
            {
                case "ASR":
                    asr = elapsed;
                    break;
                case "TRANSLATION":
                    translation = elapsed;
                    break;
                case "TTS":
                    tts = elapsed;
                    break;
                case "EXPORT":
                    mixing = elapsed;
                    break;
            }
        }

        TimeSpan total = result.EndTime - result.StartTime;

        return new DubbingBenchmarkReport(
            InputPath: options.InputPath,
            TargetLanguage: options.TargetLanguage,
            TotalDuration: total,
            AsrDuration: asr,
            TranslationDuration: translation,
            TtsDuration: tts,
            MixingDuration: mixing,
            SegmentCount: segmentCount,
            HardwareInfo: hardwareInfo,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: result.EndTime,
            StageOutcomes: result.StageOutcomes,
            Error: result.OverallStatus != DubbingRunStatus.Succeeded
                ? BuildErrorReason(result)
                : null);
    }

    private static string? BuildErrorReason(DubbingRunResult result)
    {
        var reasons = new List<string>();

        if (result.PreFlightFailures is { Count: > 0 })
            reasons.AddRange(result.PreFlightFailures);

        foreach (var outcome in result.StageOutcomes)
        {
            if (outcome.Status == StageStatus.Failed && outcome.ReasonCode is not null)
                reasons.Add($"{outcome.StageName}: {outcome.ReasonCode}");
        }

        if (reasons.Count > 0)
            return string.Join("; ", reasons);

        return result.OverallStatus switch
        {
            DubbingRunStatus.PreFlightFailed => "Pre-flight validation failed.",
            DubbingRunStatus.PartialSuccess => "Pipeline completed with partial success.",
            DubbingRunStatus.Failed => "Pipeline failed.",
            _ => "Pipeline did not succeed.",
        };
    }

    /// <summary>
    /// Builds a failure report for a run that never started (e.g. host initialization failed),
    /// so <c>StartedAtUtc</c> and <c>CompletedAtUtc</c> both reflect the moment of failure.
    /// </summary>
    internal static DubbingBenchmarkReport CreateFailureReport(
        DubbingBenchmarkOptions options,
        string hardwareInfo,
        string error) =>
        FailedReport(options, hardwareInfo, DateTimeOffset.UtcNow, error);

    private static DubbingBenchmarkReport FailedReport(
        DubbingBenchmarkOptions options,
        string hardwareInfo,
        DateTimeOffset startedAtUtc,
        string error)
    {
        return new DubbingBenchmarkReport(
            InputPath: options.InputPath,
            TargetLanguage: options.TargetLanguage,
            TotalDuration: DateTimeOffset.UtcNow - startedAtUtc,
            AsrDuration: TimeSpan.Zero,
            TranslationDuration: TimeSpan.Zero,
            TtsDuration: TimeSpan.Zero,
            MixingDuration: TimeSpan.Zero,
            SegmentCount: 0,
            HardwareInfo: hardwareInfo,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Error: error);
    }
}
