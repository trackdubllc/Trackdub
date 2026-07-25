using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Infrastructure.Logging;
using Trackdub.Infrastructure.Retry;

namespace Trackdub.Infrastructure.FileSystem;

public sealed class FileSystemArtifactStore : IArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string projectRootPath;
    private readonly IApplicationLogger logger;

    public FileSystemArtifactStore(string projectRootPath, IApplicationLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        this.projectRootPath = Path.GetFullPath(projectRootPath);
        this.logger = logger ?? new DebugApplicationLogger();
    }

    public Task EnsureLayoutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogInformation($"Ensuring artifact store layout at '{projectRootPath}'");

        Directory.CreateDirectory(projectRootPath);
        foreach (string relativeDirectory in ProjectArtifactPaths.RequiredDirectories)
        {
            Directory.CreateDirectory(GetPath(relativeDirectory));
        }

        return Task.CompletedTask;
    }

    public ArtifactWriteHandle CreateWriteHandle(string relativePath)
    {
        string normalizedRelativePath = NormalizeRelativePath(relativePath);
        string finalPath = GetPath(normalizedRelativePath);
        string extension = Path.GetExtension(finalPath);
        string tempFileName = $"{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp{extension}";
        string tempPath = Path.Combine(GetPath("temp"), tempFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        logger.LogDebug($"Created write handle: relative='{relativePath}' temp='{tempFileName}'");
        return new ArtifactWriteHandle(normalizedRelativePath, finalPath, tempPath);
    }

    public async Task CommitAsync(ArtifactWriteHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(handle.TemporaryPath))
        {
            throw new FileNotFoundException("Temporary artifact file was not created.", handle.TemporaryPath);
        }

        logger.LogDebug($"Committing artifact: '{handle.RelativePath}'");

        Directory.CreateDirectory(Path.GetDirectoryName(handle.FinalPath)!);
        await RetryHelper.ExecuteAsync(
            async _ =>
            {
                File.Move(handle.TemporaryPath, handle.FinalPath, overwrite: true);
                return true;
            },
            RetryPolicy.FileSystem,
            IsTransientCommitFailure,
            (attempt, ex) => logger.LogWarning(
                $"Artifact commit for '{handle.RelativePath}' was blocked by a transient file access error. Retrying attempt {attempt}.",
                ex),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
    {
        logger.LogDebug($"Writing JSON artifact: '{relativePath}'");

        await using ArtifactWriteHandle handle = CreateWriteHandle(relativePath);
        await using (var stream = new FileStream(
                         handle.TemporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         options: FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await CommitAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        string path = GetPath(relativePath);
        if (!File.Exists(path))
        {
            logger.LogDebug($"JSON artifact not found: '{path}'");
            return default;
        }

        try
        {
            logger.LogDebug($"Reading JSON artifact: '{relativePath}'");

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException jsonEx)
        {
            logger.LogError($"JSON deserialization error reading '{path}'", jsonEx);
            throw new InvalidDataException(
                $"Artifact '{relativePath}' exists but could not be parsed as JSON. The file may be corrupt.",
                jsonEx);
        }
        catch (IOException ioEx)
        {
            logger.LogError($"I/O error reading '{path}'", ioEx);
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning($"Read operation cancelled for '{path}'");
            throw;
        }
    }

    public string GetPath(string relativePath)
    {
        string normalizedRelativePath = NormalizeRelativePath(relativePath);
        return Path.GetFullPath(Path.Combine(projectRootPath, normalizedRelativePath));
    }

    public bool Exists(string relativePath) => File.Exists(GetPath(relativePath));

    internal static bool IsTransientCommitFailureForTesting(Exception exception) =>
        IsTransientCommitFailure(exception);

    private static bool IsTransientCommitFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string normalized = relativePath.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (IsAbsoluteArtifactPath(normalized))
        {
            throw new InvalidOperationException($"Artifact path '{relativePath}' must be project-relative.");
        }

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // Validate segments
        foreach (string segment in segments)
        {
            // Block parent directory traversal
            if (segment == "..")
            {
                throw new InvalidOperationException($"Artifact path '{relativePath}' cannot traverse parent directories.");
            }

            // Block Windows reserved device names, including names with extensions or trailing dots/spaces.
            if (IsWindowsReservedDeviceSegment(segment))
            {
                throw new InvalidOperationException($"Artifact path '{relativePath}' contains reserved Windows device name '{segment}'.");
            }

            // Block hidden files and invalid segments (starting with .)
            if (segment.StartsWith('.'))
            {
                throw new InvalidOperationException($"Artifact path '{relativePath}' contains invalid segment '{segment}'.");
            }
        }

        return Path.Combine(segments);
    }

    private static bool IsWindowsReservedDeviceSegment(string segment)
    {
        string normalized = segment.TrimEnd(' ', '.');
        if (normalized.Length == 0)
        {
            return false;
        }

        int extensionIndex = normalized.IndexOf('.');
        string deviceName = extensionIndex < 0
            ? normalized
            : normalized[..extensionIndex];
        return WindowsReservedNames.Contains(deviceName);
    }

    private static bool IsAbsoluteArtifactPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return true;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return path.Length >= 2 &&
               IsAsciiLetter(path[0]) &&
               path[1] == ':';
    }

    private static bool IsAsciiLetter(char value) =>
        (value >= 'A' && value <= 'Z') ||
        (value >= 'a' && value <= 'z');
}
