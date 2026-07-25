namespace Trackdub.Cli.Tui;

using Trackdub.Domain;

internal static class TuiMarkup
{
    internal static string Escape(string value) =>
        value
            .Replace("[", "[[", StringComparison.Ordinal)
            .Replace("]", "]]", StringComparison.Ordinal);

    internal static string FormatModelLabel(ModelInventoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.DisplayName) &&
            !string.Equals(entry.DisplayName, entry.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return entry.DisplayName;
        }

        return FormatModelSlug(entry.ModelId);
    }

    internal static string FormatModelSlug(string modelId)
    {
        int slashIndex = modelId.IndexOf('/');
        return slashIndex >= 0 && slashIndex < modelId.Length - 1
            ? modelId[(slashIndex + 1)..]
            : modelId;
    }
}
