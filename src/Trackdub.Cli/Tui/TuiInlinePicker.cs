using Spectre.Console;

namespace Trackdub.Cli.Tui;

internal sealed class TuiInlinePicker
{
    public TuiInlinePicker(string title, IReadOnlyList<(string Value, string Label)> choices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
        {
            throw new ArgumentException("At least one choice is required.", nameof(choices));
        }

        Title = title;
        Choices = choices;
    }

    public string Title { get; }

    public IReadOnlyList<(string Value, string Label)> Choices { get; }

    public int SelectedIndex { get; private set; }

    public string SelectedValue => Choices[SelectedIndex].Value;

    public void MoveUp() => SelectedIndex = Math.Max(0, SelectedIndex - 1);

    public void MoveDown() => SelectedIndex = Math.Min(Choices.Count - 1, SelectedIndex + 1);

    public void Render(IAnsiConsole console)
    {
        var lines = new List<string>
        {
            $"[bold]{TuiMarkup.Escape(Title)}[/]",
            string.Empty,
        };

        for (int index = 0; index < Choices.Count; index++)
        {
            (string _, string label) = Choices[index];
            string escaped = TuiMarkup.Escape(label);
            lines.Add(index == SelectedIndex
                ? $"  [black on grey]> {escaped}[/]"
                : $"    {escaped}");
        }

        console.Write(
            new Panel(new Markup(string.Join(Environment.NewLine, lines)))
                .BorderColor(Color.Cyan1)
                .Padding(1, 0, 1, 0));
    }
}

internal interface ITuiOverlayScreen
{
    bool HasOverlay { get; }

    void ClearOverlay();
}
