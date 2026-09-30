using System.Security.Cryptography;
using System.Text;

namespace Trackdub.Sdk;

/// <summary>
/// Resolves the per-file project directory a batch run writes into.
/// </summary>
/// <remarks>
/// Exposed for hosts that render batch output locations (the desktop report view shows where
/// each file landed) without re-deriving the folder-naming scheme. <see cref="BatchProcessor"/>
/// uses the same method internally, so a host that previews paths gets the directories the run
/// will actually use rather than a look-alike.
/// </remarks>
public static class BatchOutputPaths
{
    private const int MaxProjectFolderNameLength = 240;

    /// <summary>
    /// Builds the project directory for <paramref name="mediaFilePath"/> under
    /// <paramref name="outputRoot"/>.
    /// </summary>
    /// <param name="mediaFilePath">Absolute or relative path to the source media file.</param>
    /// <param name="outputRoot">Root directory that holds one subdirectory per media file.</param>
    /// <returns>Full path of the project directory for this media file.</returns>
    public static string BuildProjectDirectory(string mediaFilePath, string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        string fullPath = Path.GetFullPath(mediaFilePath);
        string folderName = BuildUniqueProjectFolderName(fullPath);
        return Path.Join(Path.GetFullPath(outputRoot), folderName);
    }

    /// <summary>
    /// Builds the collision-resistant folder name for a media file: a sanitized, bounded
    /// readable prefix plus a short SHA-256 prefix of the normalized full path.
    /// </summary>
    /// <param name="fullMediaPath">Full path to the source media file.</param>
    /// <returns>Folder name (no directory separators) unique to this media path.</returns>
    public static string BuildUniqueProjectFolderName(string fullMediaPath)
    {
        string normalizedPath = Path.GetFullPath(fullMediaPath);
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..8];
        string fileName = Path.GetFileName(normalizedPath);
        string? directory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrEmpty(directory))
        {
            return BuildBoundedFolderName(SanitizePathSegment(fileName), pathHash);
        }

        var segments = new List<string>();
        foreach (string segment in directory.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment is "." or "..")
            {
                continue;
            }

            if (segment.Length == 2 && segment[1] == ':')
            {
                continue;
            }

            segments.Add(SanitizePathSegment(segment));
        }

        string prefix = segments.Count > 0
            ? string.Join('_', segments) + '_'
            : string.Empty;

        return BuildBoundedFolderName($"{prefix}{SanitizePathSegment(fileName)}", pathHash);
    }

    private static string BuildBoundedFolderName(string readablePrefix, string pathHash)
    {
        string suffix = $"_{pathHash}.trackdub";
        int maxReadableLength = MaxProjectFolderNameLength - suffix.Length;
        if (maxReadableLength < 1)
        {
            return suffix;
        }

        string readable = readablePrefix;
        if (readable.Length > maxReadableLength)
        {
            readable = readable[^maxReadableLength..];
        }

        return readable + suffix;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "_";
        }

        ReadOnlySpan<char> invalidChars = Path.GetInvalidFileNameChars();
        var builder = new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            builder[i] = invalidChars.Contains(c) ? '_' : c;
        }

        return new string(builder);
    }
}
