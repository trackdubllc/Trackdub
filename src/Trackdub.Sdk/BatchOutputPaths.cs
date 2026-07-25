using System.Security.Cryptography;
using System.Text;

namespace Trackdub.Sdk;

internal static class BatchOutputPaths
{
    private const int MaxProjectFolderNameLength = 240;

    internal static string BuildProjectDirectory(string mediaFilePath, string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        string fullPath = Path.GetFullPath(mediaFilePath);
        string folderName = BuildUniqueProjectFolderName(fullPath);
        return Path.Combine(Path.GetFullPath(outputRoot), folderName);
    }

    internal static string BuildUniqueProjectFolderName(string fullMediaPath)
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
