using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Trackdub.Composition.Headless;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Kokoro;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Benchmarks;

/// <summary>One TTS synthesis job: text, language, voice and optional per-job overrides.</summary>
public sealed record TtsEvalJob(
    string Id,
    string Text,
    string LanguageCode,
    string VoiceId,
    float? Speed,
    int? WarmupRuns,
    int? RepeatRuns);

/// <summary>Per-job timing and resource record. Written as one JSON line per job.</summary>
public sealed record TtsEvalResult(
    string Id,
    int JobIndex,
    bool Ok,
    string? Error,
    double WarmupMs,
    double BestWallMs,
    double MeanWallMs,
    double AudioSeconds,
    double? Rtf,
    long WorkingSetBeforeBytes,
    long? PeakWorkingSetBytes,
    string? RequestedProvider,
    string? SelectedProvider,
    string? BootstrapDetail,
    string ModelId,
    string VoiceId);

public sealed record TtsEvalOptions(
    string JobsPath,
    string ResultsPath,
    string? Provider,
    string Model,
    string? ModelDirectory,
    string? ModelCacheDirectory,
    int WarmupRuns,
    int RepeatRuns,
    bool ShowHelp)
{
    public const int DefaultWarmupRuns = 3;
    public const int DefaultRepeatRuns = 3;

    public const string Usage =
        "tts-bench --jobs <jobs.jsonl> --results <results.jsonl> [--model <alias>] [--provider <kind>] " +
        "[--model-directory <dir>] [--model-cache-directory <dir>] [--warmup-runs <n>] [--repeat-runs <n>]";

    public static bool TryParse(IReadOnlyList<string> args, TextWriter error, out TtsEvalOptions options)
    {
        string? jobs = null, results = null, provider = null, modelDirectory = null, modelCacheDirectory = null;
        string model = KokoroTtsEngine.EngineFamilyName;
        int warmup = DefaultWarmupRuns, repeat = DefaultRepeatRuns;
        options = new TtsEvalOptions("", "", null, model, null, null, warmup, repeat, ShowHelp: false);
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (arg is "--help" or "-h")
            {
                options = options with { ShowHelp = true };
                return true;
            }
            if (index + 1 >= args.Count)
            {
                error.WriteLine($"Missing value for {arg}.");
                return false;
            }
            string value = args[++index];
            switch (arg)
            {
                case "--jobs": jobs = value; break;
                case "--results": results = value; break;
                case "--model": model = value; break;
                case "--provider": provider = value; break;
                case "--model-directory": modelDirectory = value; break;
                case "--model-cache-directory": modelCacheDirectory = value; break;
                case "--warmup-runs":
                    if (!int.TryParse(value, out warmup) || warmup < 0)
                    {
                        error.WriteLine($"Invalid --warmup-runs '{value}'. Expected a non-negative integer.");
                        return false;
                    }
                    break;
                case "--repeat-runs":
                    if (!int.TryParse(value, out repeat) || repeat < 1)
                    {
                        error.WriteLine($"Invalid --repeat-runs '{value}'. Expected a positive integer.");
                        return false;
                    }
                    break;
                default:
                    error.WriteLine($"Unknown option '{arg}'.");
                    return false;
            }
        }
        if (string.IsNullOrWhiteSpace(jobs) || string.IsNullOrWhiteSpace(results))
        {
            error.WriteLine("--jobs and --results are required.");
            return false;
        }
        ExecutionProviderKind parsedProvider = default;
        if (provider is not null && !ExecutionProviderTokens.TryParse(provider, out parsedProvider))
        {
            error.WriteLine($"Unknown provider '{provider}'. Expected one of: {ExecutionProviderTokens.FormatSupportedCliTags()}.");
            return false;
        }
        if (provider is not null)
        {
            provider = ExecutionProviderTokens.ToCanonicalTag(parsedProvider);
        }
        options = new TtsEvalOptions(jobs, results, provider, model, modelDirectory, modelCacheDirectory, warmup, repeat, ShowHelp: false);
        return true;
    }
}

/// <summary>
/// Runs a list of TTS synthesis jobs through the shipped Kokoro engine in one warm host,
/// recording wall time, RTF (wall / audio duration) and peak working set per job after
/// warmup. Best-of-N wall time is the headline figure; mean is recorded alongside so
/// cold/first-run outliers remain visible in the results file.
/// </summary>
public static class TtsEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static IReadOnlyList<TtsEvalJob> ReadJobs(TextReader reader)
    {
        var jobs = new List<TtsEvalJob>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }
            TtsEvalJob? job;
            try
            {
                job = JsonSerializer.Deserialize<TtsEvalJob>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Jobs line {lineNumber}: invalid JSON: {ex.Message}", ex);
            }
            if (job is null ||
                string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.Text) ||
                string.IsNullOrWhiteSpace(job.LanguageCode) || string.IsNullOrWhiteSpace(job.VoiceId))
            {
                throw new InvalidDataException(
                    $"Jobs line {lineNumber}: id, text, language_code and voice_id are required.");
            }
            if (!ids.Add(job.Id))
            {
                throw new InvalidDataException($"Jobs line {lineNumber}: duplicate id '{job.Id}'.");
            }
            jobs.Add(job);
        }
        return jobs;
    }

    public static async Task<int> RunAsync(
        TtsEvalOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TtsEvalJob> jobs;
        try
        {
            using var reader = new StreamReader(options.JobsPath);
            jobs = ReadJobs(reader);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        Dictionary<string, ExecutionProviderKind>? pins = null;
        if (options.Provider is not null)
        {
            if (!ExecutionProviderTokens.TryParse(options.Provider, out ExecutionProviderKind parsedProvider))
            {
                error.WriteLine($"Unknown provider '{options.Provider}'. Expected one of: {ExecutionProviderTokens.FormatSupportedCliTags()}.");
                return 1;
            }
            pins = new Dictionary<string, ExecutionProviderKind>
            {
                [RuntimeStage.Tts.ToString()] = parsedProvider,
            };
        }
        try
        {
            EnsureParentDirectory(options.ResultsPath);
            await using var results = new StreamWriter(options.ResultsPath, append: false);
            using HeadlessDubbingHost host = HeadlessDubbingHost.Create(new HeadlessTrackdubOptions
            {
                ModelDirectory = options.ModelDirectory,
                ModelCacheDirectory = options.ModelCacheDirectory,
                HardwareOverrides = pins,
                RequirePreferredExecutionProviders = pins is not null,
            });
            using IServiceScope scope = host.Services.CreateScope();
            // Use the routed engine so model selection and the runtime plan are applied before
            // invoking adapters. Some adapters intentionally reject the unplanned overload.
            if (options.Model.Equals("chatterbox", StringComparison.OrdinalIgnoreCase) ||
                options.Model.Equals("cosyvoice", StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine($"TTS model '{options.Model}' requires voice-clone request inputs not present in benchmark jobs.");
                return 1;
            }
            ITtsEngine engine = scope.ServiceProvider.GetRequiredService<RoutedTtsEngine>();
            IReadOnlyList<TtsEvalResult> all = await RunJobsAsync(
                jobs,
                engine,
                scope.ServiceProvider.GetRequiredService<IWorkingSetSampler>(),
                options,
                results,
                cancellationToken).ConfigureAwait(false);
            int failed = all.Count(r => !r.Ok);
            output.WriteLine($"tts-bench: {all.Count - failed} ok, {failed} failed; results in {options.ResultsPath}");
            return failed == 0 ? 0 : 2;
        }
        catch (IOException ex)
        {
            return ReportSetupFailure(error, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ReportSetupFailure(error, ex);
        }
        catch (ArgumentException ex)
        {
            return ReportSetupFailure(error, ex);
        }
        catch (InvalidOperationException ex)
        {
            return ReportSetupFailure(error, ex);
        }
        catch (NotSupportedException ex)
        {
            return ReportSetupFailure(error, ex);
        }
        catch (JsonException ex)
        {
            return ReportSetupFailure(error, ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ReportSetupFailure(error, ex);
        }
        catch (System.Security.SecurityException ex)
        {
            return ReportSetupFailure(error, ex);
        }
    }

    public static async Task<IReadOnlyList<TtsEvalResult>> RunJobsAsync(
        IReadOnlyList<TtsEvalJob> jobs,
        ITtsEngine engine,
        IWorkingSetSampler sampler,
        TtsEvalOptions options,
        TextWriter results,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(results);
        var all = new List<TtsEvalResult>(jobs.Count);
        for (int index = 0; index < jobs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TtsEvalJob job = jobs[index];
            TtsEvalResult result = await RunJobAsync(job, index, engine, sampler, options, cancellationToken)
                .ConfigureAwait(false);
            all.Add(result);
            await results.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
            await results.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        return all;
    }

    private static async Task<TtsEvalResult> RunJobAsync(
        TtsEvalJob job,
        int index,
        ITtsEngine engine,
        IWorkingSetSampler sampler,
        TtsEvalOptions options,
        CancellationToken cancellationToken)
    {
        long before = sampler.CaptureWorkingSetBytes();
        WorkingSetPeakMonitor? monitor = null;
        var request = new TtsSynthesisRequest(
            job.Text,
            job.LanguageCode,
            new VoiceCatalogEntry(job.VoiceId, job.LanguageCode, Gender: "n", DisplayName: job.VoiceId),
            Speed: job.Speed ?? 1.0f,
            Options: new InferenceRequestOptions(
                PreferredModelAlias: options.Model,
                PreferredExecutionProvider: options.Provider,
                RequirePreferredExecutionProvider: options.Provider is not null));
        int warmupRuns = job.WarmupRuns ?? options.WarmupRuns;
        int repeatRuns = job.RepeatRuns ?? options.RepeatRuns;
        try
        {
            double warmupMs = 0;
            TtsSynthesisResult warm = new(Array.Empty<byte>(), 0, 0, "", job.VoiceId, "");
            for (int run = 0; run < warmupRuns; run++)
            {
                var warmClock = Stopwatch.StartNew();
                warm = await engine.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
                warmClock.Stop();
                warmupMs = Math.Max(warmupMs, warmClock.Elapsed.TotalMilliseconds);
            }
            monitor = new WorkingSetPeakMonitor(sampler, before);
            var walls = new List<double>(repeatRuns);
            TtsSynthesisResult synthesized = warm;
            for (int run = 0; run < repeatRuns; run++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                synthesized = await engine.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
                clock.Stop();
                walls.Add(clock.Elapsed.TotalMilliseconds);
            }
            long? peak = monitor.Stop();
            StageRuntimeExecutionSummary? summary = (engine as IStageRuntimeExecutionReporter)?.LastExecutionSummary;
            double audioSeconds = synthesized.DurationSamples > 0 && synthesized.SampleRate > 0
                ? (double)synthesized.DurationSamples / synthesized.SampleRate
                : 0;
            double bestWallMs = walls.Count > 0 ? walls.Min() : 0;
            double meanWallMs = walls.Count > 0 ? walls.Average() : 0;
            return new TtsEvalResult(
                job.Id, index, Ok: true, Error: null,
                warmupMs, bestWallMs, meanWallMs, audioSeconds,
                Rtf: audioSeconds > 0 ? bestWallMs / 1000.0 / audioSeconds : null,
                before, peak,
                summary?.RequestedProvider, summary?.SelectedProvider, summary?.BootstrapDetail,
                synthesized.ModelId, synthesized.VoiceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            monitor?.Stop();
            throw;
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException or
            TimeoutException or JsonException or FileNotFoundException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return CreateFailedResult(job, index, before, monitor, ex);
        }
        finally
        {
            monitor?.Stop();
        }
    }

    private static TtsEvalResult CreateFailedResult(
        TtsEvalJob job,
        int index,
        long before,
        WorkingSetPeakMonitor? monitor,
        Exception exception)
    {
        long? peak = monitor?.Stop();
        return new TtsEvalResult(
            job.Id, index, Ok: false, Error: exception.Message,
            WarmupMs: 0, BestWallMs: 0, MeanWallMs: 0, AudioSeconds: 0, Rtf: null,
            before, peak,
            RequestedProvider: null, SelectedProvider: null, BootstrapDetail: null,
            ModelId: "", VoiceId: job.VoiceId);
    }

    private static void EnsureParentDirectory(string path)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static int ReportSetupFailure(TextWriter error, Exception ex)
    {
        error.WriteLine(ex.Message);
        return 1;
    }
}
