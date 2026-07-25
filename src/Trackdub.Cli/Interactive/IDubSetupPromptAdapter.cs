namespace Trackdub.Cli.Interactive;

/// <summary>
/// Prompt surface for the dub setup wizard. Text and Spectre implementations share the same flow.
/// </summary>
internal interface IDubSetupPromptAdapter
{
    Task WriteLineAsync(string message, CancellationToken cancellationToken);

    Task<string?> PromptRequiredAsync(
        string heading,
        string prompt,
        CancellationToken cancellationToken);

    Task<string?> PromptOptionalAsync(
        string heading,
        string prompt,
        string defaultDescription,
        CancellationToken cancellationToken);

    Task<string[]> PromptModelOverridesAsync(CancellationToken cancellationToken);

    Task<string?> PromptExportFormatAsync(CancellationToken cancellationToken);
}
