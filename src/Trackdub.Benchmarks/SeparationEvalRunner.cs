using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Trackdub.Composition.Headless;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Benchmarks;

/// <summary>One separation job: a 44.1 kHz PCM16 source and the two mono outputs to write.</summary>
public sealed record SeparationEvalJob(string Id, string Input, string VocalsOutput, string BedOutput);

/// <summary>Per-job timing and resource record. Written as one JSON line per job.</summary>
public sealed record SeparationEvalResult(
    string Id,
    int JobIndex,
    bool Ok,
    string? Error,
    double WallMs,
    double AudioSeconds,
    double? Rtf,
    long WorkingSetBeforeBytes,
    long? PeakWorkingSetBytes,
    string? RequestedProvider,
    string? SelectedProvider,
    string? BootstrapDetail,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record SeparationEvalOptions(
    string JobsPath,
    string ResultsPath,
    string? Provider,
    string Model,
    string? ModelDirectory,
    string? ModelCacheDirectory,
    string? FfmpegPath,
    string? FfprobePath,
    bool ShowHelp)
{
    public const string Usage =
        "separation-eval --jobs <jobs.jsonl> --results <results.jsonl> [--model <alias>] [--provider <kind>] " +
        "[--model-directory <dir>] [--model-cache-directory <dir>] [--ffmpeg <path>] [--ffprobe <path>]";

    public static bool TryParse(IReadOnlyList<string> args, TextWriter error, out SeparationEvalOptions options)
    {
        string? jobs = null, results = null, provider = null, modelDirectory = null, modelCacheDirectory = null, ffmpeg = null, ffprobe = null;
        string model = SpleeterStemSeparationEngine.EngineFamilyName;
        options = new SeparationEvalOptions("", "", null, model, null, null, null, null, ShowHelp: false);

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
                case "--ffmpeg": ffmpeg = value; break;
                case "--ffprobe": ffprobe = value; break;
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

        options = new SeparationEvalOptions(jobs, results, provider, model, modelDirectory, modelCacheDirectory, ffmpeg, ffprobe, ShowHelp: false);
        return true;
    }
}

/// <summary>
/// Runs a list of separation jobs through the shipped stem-separation engine in one warm host,
/// recording wall time, RTF and peak working set per job. Scoring is done elsewhere
/// (tools/separation-eval); this only produces stems and resource evidence.
/// </summary>
public static class SeparationEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static IReadOnlyList<SeparationEvalJob> ReadJobs(TextReader reader)
    {
        var jobs = new List<SeparationEvalJob>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            SeparationEvalJob? job;
            try
            {
                job = JsonSerializer.Deserialize<SeparationEvalJob>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Jobs line {lineNumber}: invalid JSON: {ex.Message}", ex);
            }

            if (job is null ||
                string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.Input) ||
                string.IsNullOrWhiteSpace(job.VocalsOutput) || string.IsNullOrWhiteSpace(job.BedOutput))
            {
                throw new InvalidDataException(
                    $"Jobs line {lineNumber}: id, input, vocals_output and bed_output are required.");
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
        SeparationEvalOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SeparationEvalJob> jobs;
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
                [RuntimeStage.Separation.ToString()] = parsedProvider,
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
                FfmpegPath = options.FfmpegPath,
                FfprobePath = options.FfprobePath,
                HardwareOverrides = pins,
                RequirePreferredExecutionProviders = pins is not null,
            });

            using IServiceScope scope = host.Services.CreateScope();
            IStemSeparationEngineAdapter? engine = scope.ServiceProvider
                .GetServices<IStemSeparationEngineAdapter>()
                .FirstOrDefault(e => string.Equals(e.EngineFamily, SpleeterStemSeparationEngine.EngineFamilyName, StringComparison.OrdinalIgnoreCase));
            if (engine is null)
            {
                error.WriteLine("No Spleeter stem-separation engine is registered.");
                return 1;
            }

            IReadOnlyList<SeparationEvalResult> all = await RunJobsAsync(
                jobs,
                engine,
                scope.ServiceProvider.GetRequiredService<IWorkingSetSampler>(),
                options.Model,
                options.Provider,
                results,
                cancellationToken).ConfigureAwait(false);

            int failed = all.Count(r => !r.Ok);
            output.WriteLine($"separation-eval: {all.Count - failed} ok, {failed} failed; results in {options.ResultsPath}");
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

    public static async Task<IReadOnlyList<SeparationEvalResult>> RunJobsAsync(
        IReadOnlyList<SeparationEvalJob> jobs,
        IStemSeparationEngineAdapter engine,
        IWorkingSetSampler sampler,
        string model,
        string? provider,
        TextWriter results,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(results);

        var all = new List<SeparationEvalResult>(jobs.Count);
        for (int index = 0; index < jobs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SeparationEvalJob job = jobs[index];
            SeparationEvalResult result = await RunJobAsync(job, index, engine, sampler, model, provider, cancellationToken)
                .ConfigureAwait(false);
            all.Add(result);
            await results.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
            await results.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return all;
    }

    private static async Task<SeparationEvalResult> RunJobAsync(
        SeparationEvalJob job,
        int index,
        IStemSeparationEngineAdapter engine,
        IWorkingSetSampler sampler,
        string model,
        string? provider,
        CancellationToken cancellationToken)
    {
        long before = 0;
        IWorkingSetPeakMonitor? monitor = null;
        var monitorFactory = new WorkingSetPeakMonitorFactory();
        var clock = Stopwatch.StartNew();
        try
        {
            EnsureParentDirectory(job.VocalsOutput);
            EnsureParentDirectory(job.BedOutput);

            var request = new StemSeparationRequest(
                SourceAudioPath: job.Input,
                VocalsOutputPath: job.VocalsOutput,
                AmbianceOutputPath: job.BedOutput,
                PreferredModelAlias: model,
                PreferredExecutionProvider: provider,
                RequirePreferredExecutionProvider: provider is not null);

            before = sampler.CaptureWorkingSetBytes();
            monitor = monitorFactory.Create(sampler, before);
            StemSeparationResult separated = await engine.SeparateAsync(request, progress: null, cancellationToken)
                .ConfigureAwait(false);
            clock.Stop();
            long? peak = monitor.Stop();

            StageRuntimeExecutionSummary? summary = (engine as IStageRuntimeExecutionReporter)?.LastExecutionSummary;
            double wallMs = clock.Elapsed.TotalMilliseconds;
            return new SeparationEvalResult(
                job.Id, index, Ok: true, Error: null, wallMs, separated.DurationSeconds,
                Rtf: separated.DurationSeconds > 0 ? wallMs / 1000.0 / separated.DurationSeconds : null,
                before, peak,
                summary?.RequestedProvider, summary?.SelectedProvider, summary?.BootstrapDetail,
                separated.Metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            monitor?.Stop();
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (IOException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (InvalidOperationException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (ArgumentException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (JsonException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (NotSupportedException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (TimeoutException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        catch (System.Security.SecurityException ex)
        {
            return CreateFailedResult(job, index, provider, before, clock, monitor, ex);
        }
        finally
        {
            monitor?.Stop();
        }
    }

    private static SeparationEvalResult CreateFailedResult(
        SeparationEvalJob job,
        int index,
        string? provider,
        long before,
        Stopwatch clock,
        IWorkingSetPeakMonitor? monitor,
        Exception exception)
    {
        clock.Stop();
        long? peak = monitor?.Stop();
        return new SeparationEvalResult(
            job.Id, index, Ok: false, Error: exception.Message, clock.Elapsed.TotalMilliseconds, AudioSeconds: 0,
            Rtf: null, before, peak, RequestedProvider: provider, SelectedProvider: null,
            BootstrapDetail: null, Metadata: null);
    }

    private static int ReportSetupFailure(TextWriter error, Exception exception)
    {
        error.WriteLine($"separation-eval setup failed: {exception.Message}");
        return 1;
    }

    private static void EnsureParentDirectory(string path) =>
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
}
