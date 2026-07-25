using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Trackdub.Sdk;

/// <summary>
/// Static helpers for discovering supported media files from a directory or glob pattern.
/// </summary>
public static class BatchFileDiscovery
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".wav", ".flac", ".mp3",
    };

    private const int MaxBatchSize = 10_000;

    /// <summary>
    /// Discover media files in a directory, optionally recursive.
    /// Returns sorted list (OrdinalIgnoreCase by file name).
    /// </summary>
    /// <param name="path">Directory path to scan.</param>
    /// <param name="recursive">When true, scan subdirectories recursively.</param>
    /// <returns>Full paths of discovered media files, sorted by file name (OrdinalIgnoreCase).</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when <paramref name="path"/> does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when matched files exceed the 10,000 file batch limit.</exception>
    public static IReadOnlyList<string> FromDirectory(string path, bool recursive)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var files = Directory.EnumerateFiles(path, "*", searchOption)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        ValidateBatchSize(files.Count);

        files.Sort((a, b) =>
        {
            int nameCompare = string.Compare(
                Path.GetFileName(a),
                Path.GetFileName(b),
                StringComparison.OrdinalIgnoreCase);
            if (nameCompare != 0)
            {
                return nameCompare;
            }

            int pathCompare = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return pathCompare != 0
                ? pathCompare
                : string.Compare(a, b, StringComparison.Ordinal);
        });

        return files;
    }

    /// <summary>
    /// Discover media files matching a glob pattern relative to a base directory.
    /// Returns sorted list (OrdinalIgnoreCase by full normalized path).
    /// </summary>
    /// <param name="pattern">Glob pattern to expand.</param>
    /// <param name="baseDirectory">Base directory for pattern resolution.</param>
    /// <returns>Full normalized paths of discovered media files, sorted by full path (OrdinalIgnoreCase).</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when <paramref name="baseDirectory"/> does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when matched files exceed the 10,000 file batch limit.</exception>
    public static IReadOnlyList<string> FromGlob(string pattern, string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory))
        {
            throw new DirectoryNotFoundException($"Directory not found: {baseDirectory}");
        }

        var matcher = new Matcher();
        matcher.AddInclude(pattern);

        var directoryInfo = new DirectoryInfoWrapper(new DirectoryInfo(baseDirectory));
        var result = matcher.Execute(directoryInfo);

        var files = result.Files
            .Select(match => Path.GetFullPath(Path.Combine(baseDirectory, match.Path)))
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        ValidateBatchSize(files.Count);

        files.Sort(StringComparer.OrdinalIgnoreCase);

        return files;
    }

    internal static void ValidateBatchSize(int fileCount)
    {
        if (fileCount > MaxBatchSize)
        {
            throw new InvalidOperationException(
                $"Batch size {fileCount} exceeds the maximum allowed batch size of {MaxBatchSize} files.");
        }
    }
}
