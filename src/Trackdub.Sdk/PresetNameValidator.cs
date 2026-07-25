using System.Text.RegularExpressions;

namespace Trackdub.Sdk;

/// <summary>
/// Validates preset names against the allowed character set and length constraints.
/// </summary>
public static partial class PresetNameValidator
{
    /// <summary>
    /// Determines whether the specified name is a valid preset name.
    /// Valid names contain only alphanumeric characters, hyphens, and underscores,
    /// with a length between 1 and 64 characters.
    /// </summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && ValidPattern().IsMatch(name);

    [GeneratedRegex(@"^[a-zA-Z0-9\-_]{1,64}$")]
    private static partial Regex ValidPattern();
}
