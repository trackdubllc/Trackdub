using Spectre.Console;

namespace Trackdub.Cli.Interactive;

/// <summary>
/// Spectre.Console prompts for interactive TTY dub setup. Writes to stderr so stdout stays machine-readable.
/// </summary>
internal sealed class SpectreDubSetupPromptAdapter : IDubSetupPromptAdapter
{
    private static readonly string[] s_targetLanguages =
    [
        "es",
        "fr",
        "de",
        "en",
        "pt",
        "it",
        "ja",
        "ko",
        "zh-Hans",
    ];

    private readonly IAnsiConsole _console;

    public SpectreDubSetupPromptAdapter()
        : this(SpectreStderrConsole.Create())
    {
    }

    internal SpectreDubSetupPromptAdapter(IAnsiConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public Task WriteLineAsync(string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _console.MarkupLine(message);
        return Task.CompletedTask;
    }

    public Task<string?> PromptRequiredAsync(
        string heading,
        string prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _console.WriteLine();
        _console.MarkupLine($"[bold]{Markup.Escape(heading)}[/]");

        if (heading.Contains("Target language", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(PromptTargetLanguage());
        }

        string? value = _console.Prompt(
            new TextPrompt<string>($"{Markup.Escape(prompt)}:")
                .Validate(input =>
                {
                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        return ValidationResult.Success();
                    }

                    return ValidationResult.Error($"{prompt} is required.");
                }));

        return Task.FromResult<string?>(value.Trim());
    }

    public Task<string?> PromptOptionalAsync(
        string heading,
        string prompt,
        string defaultDescription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _console.WriteLine();
        _console.MarkupLine($"[bold]{Markup.Escape(heading)}[/]");

        string? value = _console.Prompt(
            new TextPrompt<string>($"{Markup.Escape(prompt)} [[{Markup.Escape(defaultDescription)}]]:")
                .AllowEmpty());

        return Task.FromResult(BlankToNull(value));
    }

    public Task<string[]> PromptModelOverridesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _console.WriteLine();
        _console.MarkupLine("[bold]Model overrides[/]");
        _console.MarkupLine("Add stage model overrides as [grey]stage:alias[/]. Examples: asr:large-v3, translation:madlad, tts:xtts-v2.");
        _console.MarkupLine("Leave blank to use default models.");

        var overrides = new List<string>();
        while (true)
        {
            string? value = _console.Prompt(
                new TextPrompt<string>("Model override:")
                    .AllowEmpty());

            value = BlankToNull(value);
            if (value is null)
            {
                break;
            }

            if (!IsValidModelOverride(value))
            {
                _console.MarkupLine("[red]Expected format: stage:alias.[/]");
                continue;
            }

            overrides.Add(value);
        }

        return Task.FromResult(overrides.ToArray());
    }

    public Task<string?> PromptExportFormatAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _console.WriteLine();
        _console.MarkupLine("[bold]Setup stage 5/5 - Export format[/]");

        string value = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("Export container format")
                .AddChoices("mp4", "mkv")
                .UseConverter(choice => choice));

        return Task.FromResult<string?>(value);
    }

    private string PromptTargetLanguage()
    {
        return _console.Prompt(
            new SelectionPrompt<string>()
                .Title("Target language BCP-47 code")
                .PageSize(10)
                .AddChoices(s_targetLanguages)
                .UseConverter(code => code switch
                {
                    "es" => "es - Spanish",
                    "fr" => "fr - French",
                    "de" => "de - German",
                    "en" => "en - English",
                    "pt" => "pt - Portuguese",
                    "it" => "it - Italian",
                    "ja" => "ja - Japanese",
                    "ko" => "ko - Korean",
                    "zh-Hans" => "zh-Hans - Chinese (Simplified)",
                    _ => code,
                }));
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
