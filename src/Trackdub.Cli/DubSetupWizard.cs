using Trackdub.Cli.Interactive;

namespace Trackdub.Cli;

internal sealed record DubSetupRequest(
    string? MediaPath,
    string? TargetLanguage,
    string? SourceLanguage,
    string? OutputDirectory,
    string[] ModelOverrides,
    string? ExportFormat);

internal static class DubSetupWizard
{
    public static bool RequiresSetup(DubSetupRequest request) =>
        string.IsNullOrWhiteSpace(request.MediaPath)
        || string.IsNullOrWhiteSpace(request.TargetLanguage);

    public static Task<DubSetupRequest?> CompleteAsync(
        DubSetupRequest request,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken) =>
        CompleteAsync(
            request,
            new TextReaderDubSetupPromptAdapter(input, output),
            cancellationToken);

    public static async Task<DubSetupRequest?> CompleteAsync(
        DubSetupRequest request,
        IDubSetupPromptAdapter prompts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompts);

        await prompts.WriteLineAsync("Trackdub dub setup", cancellationToken).ConfigureAwait(false);
        await prompts.WriteLineAsync(
            "Prompts are written to stderr; stdout remains reserved for machine-readable command output.",
            cancellationToken).ConfigureAwait(false);
        await prompts.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);

        string? mediaPath = request.MediaPath;
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            mediaPath = await prompts.PromptRequiredAsync(
                "Setup stage 1/5 - Source media",
                "Source media path",
                cancellationToken).ConfigureAwait(false);

            if (mediaPath is null)
            {
                return null;
            }
        }

        string? targetLanguage = request.TargetLanguage;
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            targetLanguage = await prompts.PromptRequiredAsync(
                "Setup stage 2/5 - Target language",
                "Target language BCP-47 code (e.g., es, fr, de)",
                cancellationToken).ConfigureAwait(false);

            if (targetLanguage is null)
            {
                return null;
            }
        }

        string? sourceLanguage = request.SourceLanguage;
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            sourceLanguage = await prompts.PromptOptionalAsync(
                "Setup stage 3/5 - Source language",
                "Source language BCP-47 code",
                "auto-detect",
                cancellationToken).ConfigureAwait(false);
        }

        string? outputDirectory = request.OutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = await prompts.PromptOptionalAsync(
                "Setup stage 4/5 - Project output",
                "Output directory",
                "next to media as <media-stem>.trackdub",
                cancellationToken).ConfigureAwait(false);
        }

        string[] modelOverrides = request.ModelOverrides.Length > 0
            ? request.ModelOverrides
            : await prompts.PromptModelOverridesAsync(cancellationToken).ConfigureAwait(false);

        string? exportFormat = request.ExportFormat;
        if (string.IsNullOrWhiteSpace(exportFormat))
        {
            exportFormat = await prompts.PromptExportFormatAsync(cancellationToken).ConfigureAwait(false);
        }

        await prompts.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await prompts.WriteLineAsync("Setup complete. Starting dub pipeline.", cancellationToken).ConfigureAwait(false);

        return request with
        {
            MediaPath = mediaPath,
            TargetLanguage = targetLanguage,
            SourceLanguage = BlankToNull(sourceLanguage),
            OutputDirectory = BlankToNull(outputDirectory),
            ModelOverrides = modelOverrides,
            ExportFormat = BlankToNull(exportFormat),
        };
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
