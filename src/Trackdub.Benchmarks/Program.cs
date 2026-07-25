using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
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
                    string reportPath = Path.Combine(
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
            string reportsDir = batchOptions.OutputDirectory ?? Path.Combine(
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
                string jsonPath = Path.Combine(reportsDir, $"{safeFile}.json");
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
            string aggregatePath = Path.Combine(reportsDir, $"dubbing-batch-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
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
        return Path.Combine(directory, $"{fileNameWithoutExtension}-{sanitizedSuffix}{extension}");
    }

    private static string SanitizeFileNameSegment(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalidCharacters.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "variant" : sanitized;
    }
}
