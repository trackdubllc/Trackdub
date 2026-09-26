using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Inference;
using Trackdub.Inference.Onnx;

namespace Trackdub.Benchmarks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        int cancelSignalCount = 0;
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            if (Interlocked.Increment(ref cancelSignalCount) == 1)
            {
                eventArgs.Cancel = true;
                cancellationTokenSource.Cancel();
                return;
            }

            eventArgs.Cancel = false;
        };

        Console.CancelKeyPress += handler;
        try
        {
            return await RunAsync(
                args,
                Console.In,
                Console.Out,
                Console.Error,
                cancellationTokenSource.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            cancellationTokenSource.Dispose();
        }
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (args.Length > 0 &&
            args[0].Equals("controlled", StringComparison.OrdinalIgnoreCase))
        {
            return await RunControlledAsync(args.Skip(1).ToArray(), output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        if (args.Length > 0 &&
            args[0].Equals("controlled-matrix", StringComparison.OrdinalIgnoreCase))
        {
            return await RunControlledMatrixAsync(args.Skip(1).ToArray(), output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        if (args.Length > 0 &&
            (args[0].Equals("matrix", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("provider-matrix", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunProviderMatrixAsync(args.Skip(1).ToArray(), output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        if (args.Length > 0 &&
            args[0].Equals("audio-prep", StringComparison.OrdinalIgnoreCase))
        {
            return await RunAudioPrepAsync(args.Skip(1).ToArray(), output, error, cancellationToken).ConfigureAwait(false);
        }

        if (args.Length > 0 &&
            args[0].Equals("dubbing", StringComparison.OrdinalIgnoreCase))
        {
            return await RunDubbingBenchmarkAsync(args.Skip(1).ToArray(), output, error, cancellationToken).ConfigureAwait(false);
        }

        if (!BenchmarkOptions.TryParse(args, error, out var options))
        {
            BenchmarkConsole.WriteUsage(error);
            return 1;
        }

        if (options.ShowHelp)
        {
            BenchmarkConsole.WriteUsage(output);
            return 0;
        }

        try
        {
            BenchmarkOnnxExecutionBootstrap.ConfigureExecution(options);
            IModelBenchmarkRunner? runner = BenchmarkOnnxExecutionBootstrap.CreateOnnxRunner();
            if (runner is null)
            {
                error.WriteLine("ONNX model benchmarks require the Windows target framework (net10.0-windows). The audio-prep benchmark is available on all platforms.");
                return 1;
            }

            var resolver = BenchmarkModelPathResolver.CreateDefault();

            if (string.Equals(options.Scope, TrtRtxSmokeCatalog.ScopeName, StringComparison.OrdinalIgnoreCase))
            {
                return await RunTrtRtxSmokeScopeAsync(options, resolver, runner, output, error, cancellationToken);
            }

            var defaultsStore = BenchmarkSelectionDefaultsStore.LoadDefault();

            if (options.AllVariants)
            {
                return await RunAllVariantsAsync(options, resolver, runner, output, cancellationToken);
            }

            BenchmarkModelCandidate candidate = await ResolveSingleCandidateAsync(
                options,
                resolver,
                defaultsStore,
                input,
                output,
                cancellationToken);

            var request = new BenchmarkRequest(
                candidate.ModelPath,
                options.OutputPath,
                options.ProviderPreference,
                options.RunCount,
                options.WindowsMlDevicePolicyKey);

            BenchmarkReport report = await runner.RunAsync(request, cancellationToken);
            report = AddResolutionNote(report, candidate);
            await BenchmarkReportWriter.WriteAsync(report, options.ReportFormat, cancellationToken);

            if (options.ReportFormat is ReportFormat.Console or ReportFormat.Both)
            {
                BenchmarkConsole.WriteSummary(report, output);
            }

            if (options.ReportFormat is ReportFormat.Json or ReportFormat.Both)
            {
                output.WriteLine($"Report written to: {report.ReportPath}");
            }

            return report.Status is BenchmarkStatus.Failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunControlledAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            output.WriteLine("controlled <fixture> --output <directory> [--stage <name>] [--model <alias>] [--provider <kind>] [--mode fresh-process|warm-host|artifact-resume] [--reuse-engine-cache] [--language <code>] [--source-language <code>] [--runs <count>] [--mock] [--dry-run] [--report-dir <directory>]");
            return args.Length == 0 ? 1 : 0;
        }
        string? outputDirectory = null, stage = null, model = null, provider = null;
        string? sourceLanguage = null, modelDirectory = null, ffmpeg = null, ffprobe = null;
        string? expectedSha256 = null;
        string? reportDirectory = null;
        string language = "es", mode = "fresh-process";
        bool reuseCache = false;
        bool mock = false;
        bool dryRun = false;
        int runCount = 1;
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--reuse-engine-cache")
            {
                reuseCache = true;
                continue;
            }
            if (args[index] == "--mock")
            {
                mock = true;
                continue;
            }
            if (args[index] == "--dry-run")
            {
                dryRun = true;
                mock = true;
                continue;
            }
            if (index + 1 >= args.Length)
            {
                error.WriteLine($"Missing value for {args[index]}.");
                return 1;
            }
            string value = args[++index];
            switch (args[index - 1])
            {
                case "--output": outputDirectory = value; break;
                case "--stage": stage = value; break;
                case "--model": model = value; break;
                case "--provider": provider = value; break;
                case "--mode": mode = value; break;
                case "--language": language = value; break;
                case "--source-language": sourceLanguage = value; break;
                case "--model-directory": modelDirectory = value; break;
                case "--ffmpeg": ffmpeg = value; break;
                case "--ffprobe": ffprobe = value; break;
                case "--sha256": expectedSha256 = value; break;
                case "--report-dir": reportDirectory = value; break;
                case "--runs":
                    if (!int.TryParse(value, out int parsedRuns) || parsedRuns <= 0)
                    {
                        error.WriteLine($"Invalid run count '{value}'. Expected a positive integer.");
                        return 1;
                    }
                    runCount = parsedRuns;
                    break;
                default:
                    error.WriteLine($"Unknown option {args[index - 1]}.");
                    return 1;
            }
        }
        if (outputDirectory is null)
        {
            error.WriteLine("--output is required.");
            return 1;
        }
        try
        {
            var report = await new ControlledDubbingBenchmarkRunner().RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = args[0],
                    ExpectedFixtureSha256 = expectedSha256,
                    OutputDirectory = outputDirectory,
                    Stage = stage,
                    Model = model,
                    Provider = provider,
                    Mode = mode,
                    ReuseEngineCache = reuseCache,
                    TargetLanguage = language,
                    SourceLanguage = sourceLanguage,
                    ModelDirectory = modelDirectory,
                    FfmpegPath = ffmpeg,
                    FfprobePath = ffprobe,
                    RunCount = runCount,
                    Mock = mock,
                    DryRun = dryRun,
                }, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Evidence {report.RunId:N}: {report.Status} ({report.RunMode}, {report.Scenario})");
            if (report.Reason is not null) output.WriteLine(report.Reason);

            if (!string.IsNullOrWhiteSpace(reportDirectory))
            {
                await Reports.BenchmarkReportExporter.ExportAllAsync(
                    report, reportDirectory, "benchmark-evidence", cancellationToken).ConfigureAwait(false);
                output.WriteLine($"Reports exported to: {reportDirectory}");
            }

            return report.Status == Trackdub.Contracts.Benchmarking.BenchmarkEvidenceStatus.Completed ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunProviderMatrixAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h" or "/?"))
        {
            output.WriteLine("matrix <fixture> --output <directory> [--providers <comma-separated>] [--baseline <provider>] [--scenario <name>] [--runs <count>] [--mock] [--dry-run] [--format <console|json|both>] [--report-dir <directory>]");
            output.WriteLine("Evaluates comparative execution provider performance (speedup, latency deltas, throughput, memory) across configured providers.");
            return args.Length == 0 ? 1 : 0;
        }

        string fixturePath = args[0];
        string? outputDirectory = null;
        string? scenario = "full-pipeline";
        string baselineProvider = "cpu";
        IReadOnlyList<string>? providers = null;
        int runCount = 1;
        bool mock = false;
        bool dryRun = false;
        ReportFormat format = ReportFormat.Both;
        string? reportDirectory = null;

        for (int index = 1; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg == "--mock")
            {
                mock = true;
                continue;
            }
            if (arg == "--dry-run")
            {
                dryRun = true;
                mock = true;
                continue;
            }
            if (index + 1 >= args.Length)
            {
                error.WriteLine($"Missing value for {arg}.");
                return 1;
            }
            string value = args[++index];
            switch (arg)
            {
                case "--output":
                    outputDirectory = value;
                    break;
                case "--scenario":
                    scenario = value;
                    break;
                case "--baseline":
                    baselineProvider = value;
                    break;
                case "--providers":
                    providers = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--runs":
                    if (!int.TryParse(value, out int parsedRuns) || parsedRuns <= 0)
                    {
                        error.WriteLine($"Invalid run count '{value}'. Expected a positive integer.");
                        return 1;
                    }
                    runCount = parsedRuns;
                    break;
                case "--format":
                    if (!Enum.TryParse(value, ignoreCase: true, out format))
                    {
                        error.WriteLine($"Unknown format '{value}'. Expected console, json, or both.");
                        return 1;
                    }
                    break;
                case "--report-dir":
                    reportDirectory = value;
                    break;
                default:
                    error.WriteLine($"Unknown option {arg}.");
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            error.WriteLine("--output is required.");
            return 1;
        }

        providers ??= ["cpu", "directml"];

        string[] validProviders = ["cpu", "dml", "directml", "cuda", "tensorrt", "trt", "tensorrtrtx", "trt-rtx", "openvino", "migraphx", "rocm", "coreml", "nnapi", "xnnpack", "snpe", "qnn"];
        foreach (string p in providers)
        {
            if (!validProviders.Contains(BenchmarkComparison.NormalizeProvider(p), StringComparer.OrdinalIgnoreCase) &&
                !validProviders.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                error.WriteLine($"Unknown provider '{p}'. Expected one of: cpu, directml, cuda, tensorrt, openvino, etc.");
                return 1;
            }
        }

        if (!providers.Any(p => BenchmarkComparison.NormalizeProvider(p).Equals(
                BenchmarkComparison.NormalizeProvider(baselineProvider), StringComparison.OrdinalIgnoreCase)))
        {
            error.WriteLine($"Baseline provider '{baselineProvider}' must be one of --providers ({string.Join(", ", providers)}).");
            return 1;
        }

        string effectiveScenario = scenario ?? "full-pipeline";
        if (!mock && !dryRun && effectiveScenario.Equals("full-pipeline", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine(
                "The default 'full-pipeline' scenario requires --mock or --dry-run for a real (non-mock) run, " +
                "because a whole-pipeline provider pin is not supported outside mock mode. Pass --scenario " +
                "with a specific stage name, or run with --mock.");
            return 1;
        }

        try
        {
            using var runner = new Trackdub.Benchmarks.Scenarios.ExecutionProviderMatrixRunner();
            var options = new Trackdub.Benchmarks.Scenarios.ExecutionProviderMatrixOptions
            {
                FixturePath = fixturePath,
                OutputDirectory = outputDirectory,
                Scenario = effectiveScenario,
                BaselineProvider = baselineProvider,
                Providers = providers,
                RunCount = runCount,
                Mock = mock || dryRun,
                DryRun = dryRun,
                Format = format,
            };

            var report = await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);

            if (report.SkippedProviders.Count > 0)
            {
                error.WriteLine(
                    $"Provider(s) skipped from comparison (failed, incomplete, or fell back to a different " +
                    $"provider than requested): {string.Join(", ", report.SkippedProviders)}.");
                return 1;
            }

            if (format is ReportFormat.Console or ReportFormat.Both)
            {
                output.WriteLine($"Execution Provider Matrix: {report.Scenario} (Baseline: {report.BaselineProvider})");
                foreach (var comp in report.Comparisons)
                {
                    output.WriteLine($"  {comp.Provider}: P50={comp.P50Milliseconds:F1}ms, Speedup={comp.SpeedupFactor:F2}x, LatencyDelta={comp.LatencyDeltaMilliseconds:F1}ms, ThroughputRatio={comp.ThroughputRatio:F2}x, MemoryDelta={comp.PeakWorkingSetDeltaBytes / (1024 * 1024):+0;-0;0}MB");
                }
            }

            if (format is ReportFormat.Json or ReportFormat.Both)
            {
                string reportPath = Path.Join(outputDirectory, "execution-provider-matrix.json");
                output.WriteLine($"Report written to: {reportPath}");
            }

            // Export Markdown alongside JSON into output dir by default, or report-dir if specified
            string exportDir = reportDirectory ?? outputDirectory;
            await Reports.BenchmarkReportExporter.ExportMarkdownAsync(
                report,
                Path.Join(exportDir, "execution-provider-matrix.md"),
                cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Markdown report written to: {Path.Join(exportDir, "execution-provider-matrix.md")}");

            if (!string.IsNullOrWhiteSpace(reportDirectory) && reportDirectory != outputDirectory)
            {
                // Also copy JSON to report dir for a single-directory archive
                await Reports.BenchmarkReportExporter.ExportJsonAsync(
                    report,
                    Path.Join(reportDirectory, "execution-provider-matrix.json"),
                    cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunControlledMatrixAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            output.WriteLine("controlled-matrix <fixture> --output <directory> [--stages <comma-separated>] [--model <stage=alias>] [--provider <kind>] [--mode fresh-process|warm-host|artifact-resume] [--reuse-engine-cache] [--language <code>] [--source-language <code>] [--runs <count>]");
            output.WriteLine("With no --stages, runs the full extended pipeline stage catalog in canonical order.");
            return args.Length == 0 ? 1 : 0;
        }

        if (!ControlledStageBenchmarkMatrixOptionsParser.TryParse(args, error, out var options) || options is null)
        {
            return 1;
        }

        try
        {
            using var runner = new ControlledStageBenchmarkMatrixRunner();
            ControlledStageBenchmarkMatrixReport report = await runner.RunAsync(options, cancellationToken)
                .ConfigureAwait(false);
            await BenchmarkReportWriter.WriteAsync(report, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Stage matrix {report.Status} ({report.Results.Count} stage(s))");
            output.WriteLine($"Report written to: {report.ReportPath}");
            foreach (ControlledStageBenchmarkMatrixResult result in report.Results)
            {
                BenchmarkEvidenceStage? stage = result.Evidence.Stages
                    .FirstOrDefault(candidate => candidate.Name.Equals(result.Stage, StringComparison.OrdinalIgnoreCase));
                output.WriteLine(
                    $"  {result.Stage}: {result.Evidence.Status}"
                    + (stage?.DurationMilliseconds is double duration ? $" ({duration:F1} ms)" : string.Empty)
                    + (!string.IsNullOrWhiteSpace(result.Evidence.Reason) ? $" - Reason: {result.Evidence.Reason}" : string.Empty));
            }

            return report.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            // Cancellation (Ctrl+C) must keep its non-zero exit semantics instead of a
            // regular failure exit; stage failures are captured in the report, so anything
            // else reaching here is unexpected and reported with its message.
            throw;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunAudioPrepAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!AudioPrepBenchmarkOptions.TryParse(args, error, out var options))
        {
            BenchmarkConsole.WriteUsage(error);
            return 1;
        }

        if (options.ShowHelp)
        {
            BenchmarkConsole.WriteUsage(output);
            return 0;
        }

        try
        {
            var runner = new AudioPrepBenchmarkRunner();
            AudioPrepBenchmarkReport report = await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);
            await BenchmarkReportWriter.WriteAsync(report, options.ReportFormat, cancellationToken).ConfigureAwait(false);

            if (options.ReportFormat is ReportFormat.Console or ReportFormat.Both)
            {
                BenchmarkConsole.WriteAudioPrepSummary(report, output);
            }

            if (options.ReportFormat is ReportFormat.Json or ReportFormat.Both)
            {
                output.WriteLine($"Audio prep benchmark report written to: {report.ReportPath}");
            }

            return report.Aggregate.AutoComparisonCount == 0 ||
                   report.Aggregate.AcceptedAutoCount == report.Aggregate.AutoComparisonCount
                ? 0
                : 1;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunDubbingBenchmarkAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        // Batch mode: dubbing --batch <videos-dir> --languages fr,de,it,ja [--source-language <code>] [--output <dir>] [--force-rerun]
        // Single mode: dubbing <input-path> [--language <code>] [--source-language <code>] [--output <dir>]
        if (args.Length > 0 && args[0].Equals("--batch", StringComparison.OrdinalIgnoreCase))
        {
            return await RunDubbingBatchAsync(args.Skip(1).ToArray(), output, error, cancellationToken);
        }

        if (!DubbingBenchmarkOptions.TryParse(args, error, out var options))
        {
            BenchmarkConsole.WriteDubbingUsage(error);
            return 1;
        }

        if (options?.ShowHelp == true)
        {
            BenchmarkConsole.WriteDubbingUsage(output);
            return 0;
        }

        try
        {
            if (options is null)
            {
                error.WriteLine("Error: Options parsing failed.");
                return 1;
            }

            var runner = new DubbingBenchmarkRunner();
            output.WriteLine($"Starting dubbing benchmark...");
            output.WriteLine($"Input: {options.InputPath}");
            output.WriteLine($"Target Language: {options.TargetLanguage}");
            output.WriteLine();

            DubbingBenchmarkReport report = await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);

            if (report.Success)
            {
                if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
                {
                    string reportPath = Path.Join(
                        options.OutputDirectory,
                        $"{Path.GetFileNameWithoutExtension(options.InputPath)}-{options.TargetLanguage}.json");
                    report = report with { ReportPath = reportPath };
                }

                await BenchmarkReportWriter.WriteAsync(report, ReportFormat.Json, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"Benchmark report written to: {report.ReportPath}");

                BenchmarkConsole.WriteDubbingSummary(report, output);
                return 0;
            }
            else
            {
                error.WriteLine($"Benchmark failed: {report.Error}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            // Entry point error handling: log full exception and exit with error code
            error.WriteLine(ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// Batch mode: process all media files in a directory through the pipeline
    /// for each requested target language.
    /// </summary>
    private static async Task<int> RunDubbingBatchAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!DubbingBatchOptions.TryParse(args, error, out DubbingBatchOptions? batchOptions))
        {
            BenchmarkConsole.WriteDubbingUsage(error);
            return 1;
        }

        if (batchOptions is null)
        {
            error.WriteLine("Error: Options parsing failed.");
            return 1;
        }

        if (batchOptions.ShowHelp)
        {
            BenchmarkConsole.WriteDubbingUsage(output);
            return 0;
        }

        try
        {
            IReadOnlyList<string> mediaFiles = batchOptions.DiscoverMediaFiles();
            if (mediaFiles.Count == 0)
            {
                error.WriteLine($"Error: No media files found in '{batchOptions.VideosDirectory}'.");
                return 1;
            }

            output.WriteLine(
                $"Batch mode: {mediaFiles.Count} video(s) × {batchOptions.TargetLanguages.Count} language(s) = " +
                $"{mediaFiles.Count * batchOptions.TargetLanguages.Count} run(s).");
            output.WriteLine($"Videos: {batchOptions.VideosDirectory}");
            output.WriteLine($"Languages: {string.Join(", ", batchOptions.TargetLanguages)}");
            output.WriteLine();

            var batchRunner = new DubbingBatchRunner();
            IReadOnlyList<DubbingBenchmarkReport> reports = await batchRunner.RunBatchAsync(
                mediaFiles,
                batchOptions.TargetLanguages,
                batchOptions.SourceLanguageCode,
                batchOptions.OutputDirectory,
                batchOptions.ForceRerun,
                cancellationToken).ConfigureAwait(false);

            // Write JSON reports and print console summary.
            string reportsDir = batchOptions.OutputDirectory ?? Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "TrackdubBenchmarks");
            Directory.CreateDirectory(reportsDir);

            var writtenReports = new List<DubbingBenchmarkReport>(reports.Count);
            foreach (DubbingBenchmarkReport report in reports)
            {
                string pathHash = DubbingBenchmarkRunner.ComputePathHash(report.InputPath);
                string baseName = Path.GetFileNameWithoutExtension(report.InputPath);
                string fileNameBase = $"{baseName}-{pathHash}-{report.TargetLanguage}";
                string safeFile = new string(fileNameBase.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
                string jsonPath = Path.Join(reportsDir, $"{safeFile}.json");
                DubbingBenchmarkReport writtenReport = report with { ReportPath = jsonPath };
                await BenchmarkReportWriter.WriteAsync(
                    writtenReport,
                    ReportFormat.Json,
                    cancellationToken).ConfigureAwait(false);
                writtenReports.Add(writtenReport);
            }

            // Print aggregate summary.
            BenchmarkConsole.WriteDubbingBatchSummary(writtenReports, output);

            // Write aggregate report.
            string aggregatePath = Path.Join(reportsDir, $"dubbing-batch-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            await using var stream = new FileStream(aggregatePath, FileMode.Create, FileAccess.Write);
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, writtenReports, BenchmarkReportWriter.SerializerOptions, cancellationToken);
            output.WriteLine();
            output.WriteLine($"Aggregate report: {aggregatePath}");

            int failCount = writtenReports.Count(r => !r.Success);
            if (failCount > 0)
            {
                error.WriteLine($"{failCount} run(s) failed out of {writtenReports.Count}.");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunAllVariantsAsync(
        BenchmarkOptions options,
        BenchmarkModelPathResolver resolver,
        IModelBenchmarkRunner runner,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        BenchmarkModelResolutionResult discovery = resolver.Discover(options.ModelPath);
        if (!string.IsNullOrWhiteSpace(discovery.Error))
        {
            throw new FileNotFoundException(discovery.Error, options.ModelPath);
        }

        if (discovery.Candidates.Count == 0)
        {
            throw new FileNotFoundException("Model path or scope did not resolve to an ONNX model.", options.ModelPath);
        }

        var reports = new List<BenchmarkReport>(discovery.Candidates.Count);
        foreach (BenchmarkModelCandidate candidate in discovery.Candidates)
        {
            string reportPath = DeriveVariantReportPath(options.OutputPath, candidate);
            var request = new BenchmarkRequest(
                candidate.ModelPath,
                reportPath,
                options.ProviderPreference,
                options.RunCount,
                options.WindowsMlDevicePolicyKey);

            BenchmarkReport report = await runner.RunAsync(request, cancellationToken);
            report = AddResolutionNote(report, candidate);
            reports.Add(report);

            await BenchmarkReportWriter.WriteAsync(report, options.ReportFormat, cancellationToken);
        }

        var batchReport = new BenchmarkBatchReport(
            RequestedReference: options.ModelPath,
            ReportPath: options.OutputPath,
            Results: reports,
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        await BenchmarkReportWriter.WriteAsync(batchReport, options.ReportFormat, cancellationToken);

        if (options.ReportFormat is ReportFormat.Console or ReportFormat.Both)
        {
            BenchmarkConsole.WriteBatchSummary(batchReport, output);
        }

        if (options.ReportFormat is ReportFormat.Json or ReportFormat.Both)
        {
            output.WriteLine($"Batch report written to: {batchReport.ReportPath}");
        }

        return reports.Any(report => report.Status is BenchmarkStatus.Failed) ? 1 : 0;
    }

    private static async Task<BenchmarkModelCandidate> ResolveSingleCandidateAsync(
        BenchmarkOptions options,
        BenchmarkModelPathResolver resolver,
        BenchmarkSelectionDefaultsStore defaultsStore,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Variant))
        {
            return resolver.ResolveSingle(options.ModelPath, options.Variant);
        }

        if (options.ModelPath.Contains('@', StringComparison.Ordinal))
        {
            return resolver.ResolveSingle(options.ModelPath);
        }

        BenchmarkModelResolutionResult discovery = resolver.Discover(options.ModelPath);
        if (!string.IsNullOrWhiteSpace(discovery.Error))
        {
            throw new FileNotFoundException(discovery.Error, options.ModelPath);
        }

        if (defaultsStore.TryGet(discovery.ScopeKey, out string? storedCandidateKey) &&
            !string.IsNullOrWhiteSpace(storedCandidateKey))
        {
            BenchmarkModelCandidate? storedCandidate = discovery.Candidates.FirstOrDefault(
                candidate => candidate.CandidateKey.Equals(storedCandidateKey, StringComparison.OrdinalIgnoreCase));

            if (storedCandidate is not null)
            {
                return storedCandidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(discovery.DefaultCandidateKey))
        {
            BenchmarkModelCandidate? defaultCandidate = discovery.Candidates.FirstOrDefault(
                candidate => candidate.CandidateKey.Equals(discovery.DefaultCandidateKey, StringComparison.OrdinalIgnoreCase));

            if (defaultCandidate is not null)
            {
                return defaultCandidate;
            }
        }

        if (discovery.Candidates.Count == 1)
        {
            return discovery.Candidates[0];
        }

        if (discovery.Candidates.Count == 0)
        {
            throw new FileNotFoundException("Model path or scope did not resolve to an ONNX model.", options.ModelPath);
        }

        BenchmarkModelCandidate selectedCandidate = await PromptForCandidateAsync(discovery, input, output, cancellationToken);
        defaultsStore.Set(discovery.ScopeKey, selectedCandidate.CandidateKey);
        await defaultsStore.SaveAsync(cancellationToken);
        return selectedCandidate;
    }

    private static async Task<BenchmarkModelCandidate> PromptForCandidateAsync(
        BenchmarkModelResolutionResult discovery,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await output.WriteLineAsync($"Multiple benchmarkable ONNX variants were found for '{discovery.RequestedReference}'.");
        for (int index = 0; index < discovery.Candidates.Count; index++)
        {
            BenchmarkModelCandidate candidate = discovery.Candidates[index];
            await output.WriteLineAsync($"{index + 1}. {candidate.DisplayName} -> {candidate.ModelPath}");
        }

        await output.WriteAsync("Choose the default variant number to remember for this machine: ");
        string? response = await input.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(response) ||
            !int.TryParse(response, out int selectedIndex) ||
            selectedIndex < 1 ||
            selectedIndex > discovery.Candidates.Count)
        {
            throw new InvalidOperationException("Ambiguous model selection requires a valid variant number.");
        }

        return discovery.Candidates[selectedIndex - 1];
    }

    private static async Task<int> RunTrtRtxSmokeScopeAsync(
        BenchmarkOptions options,
        BenchmarkModelPathResolver resolver,
        IModelBenchmarkRunner runner,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        BenchmarkProviderPreference providerPreference = options.ProviderPreference is BenchmarkProviderPreference.Cpu or BenchmarkProviderPreference.Auto
            ? BenchmarkProviderPreference.TensorRtRtx
            : options.ProviderPreference;

        int passed = 0;
        int failed = 0;
        int skipped = 0;
        var reports = new List<BenchmarkReport>(TrtRtxSmokeCatalog.StarterPackTurboGpu.Count);

        await output.WriteLineAsync($"TRT RTX smoke scope ({TrtRtxSmokeCatalog.StarterPackTurboGpu.Count} targets).").ConfigureAwait(false);

        foreach (TrtRtxSmokeCatalog.Target target in TrtRtxSmokeCatalog.StarterPackTurboGpu)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BenchmarkModelCandidate candidate;
            try
            {
                candidate = resolver.ResolveSingle(target.ModelReference, target.Variant);
            }
            catch (FileNotFoundException ex)
            {
                skipped++;
                await output.WriteLineAsync($"SKIP {target.Label}: {ex.Message}").ConfigureAwait(false);
                continue;
            }

            string reportPath = DeriveVariantReportPath(options.OutputPath, candidate with
            {
                VariantAlias = target.Label
            });

            var request = new BenchmarkRequest(
                candidate.ModelPath,
                reportPath,
                providerPreference,
                options.RunCount,
                options.WindowsMlDevicePolicyKey);

            BenchmarkReport report;
            try
            {
                report = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
                report = AddResolutionNote(report, candidate);
            }
            catch (Exception ex)
            {
                failed++;
                await error.WriteLineAsync($"FAIL {target.Label}: {ex.Message}").ConfigureAwait(false);
                continue;
            }

            reports.Add(report);
            await BenchmarkReportWriter.WriteAsync(report, options.ReportFormat, cancellationToken).ConfigureAwait(false);

            if (options.ReportFormat is ReportFormat.Console or ReportFormat.Both)
            {
                await output.WriteLineAsync($"--- {target.Label} ({target.ModelReference}) ---").ConfigureAwait(false);
                BenchmarkConsole.WriteSummary(report, output);
            }

            if (report.Status is BenchmarkStatus.Failed)
            {
                failed++;
                await error.WriteLineAsync($"FAIL {target.Label}: benchmark status {report.Status}.").ConfigureAwait(false);
            }
            else
            {
                passed++;
            }
        }

        var batchReport = new BenchmarkBatchReport(
            RequestedReference: TrtRtxSmokeCatalog.ScopeName,
            ReportPath: options.OutputPath,
            Results: reports,
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        await BenchmarkReportWriter.WriteAsync(batchReport, options.ReportFormat, cancellationToken).ConfigureAwait(false);

        if (options.ReportFormat is ReportFormat.Console or ReportFormat.Both)
        {
            BenchmarkConsole.WriteBatchSummary(batchReport, output);
            await output.WriteLineAsync(
                    $"TRT RTX smoke summary: passed={passed}, failed={failed}, skipped={skipped}.")
                .ConfigureAwait(false);
        }

        if (options.ReportFormat is ReportFormat.Json or ReportFormat.Both)
        {
            await output.WriteLineAsync($"Batch report written to: {batchReport.ReportPath}").ConfigureAwait(false);
        }

        if (passed == 0 && failed == 0)
        {
            await error.WriteLineAsync("TRT RTX smoke did not run any targets (all skipped). Download starter-pack models first.").ConfigureAwait(false);
            return 1;
        }

        return failed > 0 ? 1 : 0;
    }

    private static BenchmarkReport AddResolutionNote(BenchmarkReport report, BenchmarkModelCandidate candidate)
    {
        if (report.Notes.Any(note => note.Equals(candidate.ResolutionNote, StringComparison.Ordinal)))
        {
            return report;
        }

        return report with
        {
            Notes = new[] { candidate.ResolutionNote }.Concat(report.Notes).ToArray()
        };
    }

    private static string DeriveVariantReportPath(string aggregateReportPath, BenchmarkModelCandidate candidate)
    {
        string directory = Path.GetDirectoryName(aggregateReportPath) ?? Environment.CurrentDirectory;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(aggregateReportPath);
        string extension = Path.GetExtension(aggregateReportPath);
        string suffix = candidate.VariantAlias ?? Path.GetFileNameWithoutExtension(candidate.ModelPath);
        string sanitizedSuffix = SanitizeFileNameSegment(suffix);
        return Path.Join(directory, $"{fileNameWithoutExtension}-{sanitizedSuffix}{extension}");
    }

    private static string SanitizeFileNameSegment(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalidCharacters.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "variant" : sanitized;
    }
}
