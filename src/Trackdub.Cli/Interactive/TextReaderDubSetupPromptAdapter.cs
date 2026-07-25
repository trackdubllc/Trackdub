namespace Trackdub.Cli.Interactive;

/// <summary>
/// Readline prompts for unit tests and non-Spectre harnesses.
/// </summary>
internal sealed class TextReaderDubSetupPromptAdapter(TextReader input, TextWriter output) : IDubSetupPromptAdapter
{
    public Task WriteLineAsync(string message, CancellationToken cancellationToken) =>
        output.WriteLineAsync(message.AsMemory(), cancellationToken);

    public async Task<string?> PromptRequiredAsync(
        string heading,
        string prompt,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(heading.AsMemory(), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            await output.WriteAsync($"{prompt}: ".AsMemory(), cancellationToken).ConfigureAwait(false);
            string? value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return null;
            }

            value = value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                await output.WriteLineAsync().ConfigureAwait(false);
                return value;
            }

            await output.WriteLineAsync($"{prompt} is required.").ConfigureAwait(false);
        }
    }

    public async Task<string?> PromptOptionalAsync(
        string heading,
        string prompt,
        string defaultDescription,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(heading.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync($"{prompt} [{defaultDescription}]: ".AsMemory(), cancellationToken).ConfigureAwait(false);
        string? value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        return BlankToNull(value);
    }

    public async Task<string[]> PromptModelOverridesAsync(CancellationToken cancellationToken)
    {
        var overrides = new List<string>();

        await output.WriteLineAsync(
            "Add stage model overrides as stage:alias, one per line. Examples: asr:large-v3, translation:madlad, tts:xtts-v2.".AsMemory(),
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("Leave blank to use default models.".AsMemory(), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            await output.WriteAsync("Model override: ".AsMemory(), cancellationToken).ConfigureAwait(false);
            string? value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                break;
            }

            value = value.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                break;
            }

            if (!IsValidModelOverride(value))
            {
                await output.WriteLineAsync("Expected format: stage:alias.".AsMemory(), cancellationToken).ConfigureAwait(false);
                continue;
            }

            overrides.Add(value);
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        return [.. overrides];
    }

    public async Task<string?> PromptExportFormatAsync(CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("Setup stage 5/5 - Export format".AsMemory(), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            await output.WriteAsync("Export container format [mp4]: ".AsMemory(), cancellationToken).ConfigureAwait(false);
            string? value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return null;
            }

            value = value.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (string.Equals(value, "mp4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "mkv", StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync().ConfigureAwait(false);
                return value.ToLowerInvariant();
            }

            await output.WriteLineAsync("Choose mp4 or mkv.".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsValidModelOverride(string value)
    {
        int colonIndex = value.IndexOf(':', StringComparison.Ordinal);
        return colonIndex > 0
            && colonIndex < value.Length - 1
            && !string.IsNullOrWhiteSpace(value[..colonIndex])
            && !string.IsNullOrWhiteSpace(value[(colonIndex + 1)..]);
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
