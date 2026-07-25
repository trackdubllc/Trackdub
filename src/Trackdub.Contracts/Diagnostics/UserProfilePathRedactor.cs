using System.Text;
using System.Text.RegularExpressions;

namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Redacts the current user's profile path from diagnostic text shown in UI or written to support bundles.
/// </summary>
public static class UserProfilePathRedactor
{
    private const string UserPathTerminatorPattern = @"(?=$|[\\/]|[\s""'),.;:\]])";

    private static readonly Lazy<UserProfileRedactionContext> CurrentContext = new(CreateCurrentContext);

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        UserProfileRedactionContext context = CurrentContext.Value;
        string redacted = input;
        if (context.WindowsUserPathRegex is not null &&
            redacted.Contains(context.UserName!, StringComparison.OrdinalIgnoreCase))
        {
            redacted = context.WindowsUserPathRegex.Replace(redacted, "$1<USER>");
            redacted = context.UnixUserPathRegex!.Replace(redacted, "$1<USER>");
        }

        if (!string.IsNullOrWhiteSpace(context.UserProfilePath) &&
            redacted.Contains(context.UserProfilePath, StringComparison.OrdinalIgnoreCase))
        {
            redacted = ReplaceCaseInsensitive(redacted, context.UserProfilePath, context.UserProfileReplacement);
        }

        return redacted;
    }

    private static UserProfileRedactionContext CreateCurrentContext()
    {
        string userName = Environment.UserName;
        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Regex? windowsUserPathRegex = null;
        Regex? unixUserPathRegex = null;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            windowsUserPathRegex = new Regex(
                $@"([\\/]+Users[\\/]+){Regex.Escape(userName)}{UserPathTerminatorPattern}",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            unixUserPathRegex = new Regex(
                $@"(/Users/){Regex.Escape(userName)}{UserPathTerminatorPattern}",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        string replacement = string.IsNullOrWhiteSpace(userName)
            ? userProfilePath
            : userProfilePath.Replace(userName, "<USER>", StringComparison.OrdinalIgnoreCase);
        return new UserProfileRedactionContext(
            userName,
            userProfilePath,
            replacement,
            windowsUserPathRegex,
            unixUserPathRegex);
    }

    private static string ReplaceCaseInsensitive(string source, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(oldValue))
        {
            return source;
        }

        int startIndex = 0;
        var builder = new StringBuilder();
        while (startIndex < source.Length)
        {
            int index = source.IndexOf(oldValue, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                builder.Append(source, startIndex, source.Length - startIndex);
                break;
            }

            builder.Append(source, startIndex, index - startIndex);
            builder.Append(newValue);
            startIndex = index + oldValue.Length;
        }

        return builder.ToString();
    }

    private sealed record UserProfileRedactionContext(
        string? UserName,
        string UserProfilePath,
        string UserProfileReplacement,
        Regex? WindowsUserPathRegex,
        Regex? UnixUserPathRegex);
}
