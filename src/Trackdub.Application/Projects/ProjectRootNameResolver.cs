namespace Trackdub.Application.Projects;

public sealed record ProjectRootNameCandidate(
    string ProjectName,
    string ProjectRootPath);

public static class ProjectRootNameResolver
{
    private const int MaxCopyNumberAttempts = 100;

    private static readonly HashSet<char> InvalidProjectFolderNameChars = [.. Path.GetInvalidFileNameChars()];

    private static readonly HashSet<string> ReservedProjectFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public static string ResolveProjectParentDirectory(string mediaPath, string? userDataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        string? mediaParent = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrWhiteSpace(mediaParent))
        {
            throw new InvalidOperationException("Source media path does not have a parent directory.");
        }

        if (!IsLikelyCloudSyncedPath(mediaParent))
        {
            return mediaParent;
        }

        string trackdubRoot = string.IsNullOrWhiteSpace(userDataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trackdub")
            : userDataRoot;
        string projectsDirectory = Path.Combine(trackdubRoot, "projects");
        Directory.CreateDirectory(projectsDirectory);
        return projectsDirectory;
    }

    public static ProjectRootNameCandidate CreateAvailableProjectRoot(
        string mediaPath,
        string projectName,
        string? projectParentDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        string directory = projectParentDirectory
            ?? ResolveProjectParentDirectory(mediaPath);
        ThrowIfLikelyCloudSyncedProjectParent(directory);

        string baseName = SanitizeProjectFolderName(projectName, Path.GetFileNameWithoutExtension(mediaPath));

        for (int copyNumber = 1; copyNumber <= MaxCopyNumberAttempts; copyNumber++)
        {
            string candidateName = copyNumber == 1
                ? baseName
                : $"{baseName} #{copyNumber}";
            string candidatePath = Path.Combine(directory, $"{candidateName}.trackdub");
            if (!ProjectPathIsTaken(candidatePath))
            {
                return new ProjectRootNameCandidate(candidateName, candidatePath);
            }
        }

        throw new InvalidOperationException("Unable to create a unique project folder name.");
    }

    private static bool ProjectPathIsTaken(string candidatePath)
    {
        try
        {
            return Directory.Exists(candidatePath) || File.Exists(candidatePath);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsLikelyCloudSyncedPath(string path)
    {
        string normalized = path.Replace('/', '\\');
        return normalized.Contains(@"\OneDrive\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\Dropbox\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\Google Drive\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\iCloudDrive\", StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfLikelyCloudSyncedProjectParent(string path)
    {
        if (!IsLikelyCloudSyncedPath(path))
        {
            return;
        }

        throw new IOException(
            $"Project parent directory is in a cloud-synced folder and may block filesystem metadata checks: {path}");
    }

    private static string SanitizeProjectFolderName(string projectName, string fallbackName)
    {
        string sanitized = NormalizeProjectFolderName(projectName);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = NormalizeProjectFolderName(fallbackName);
        }

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Project";
        }

        return EscapeReservedProjectFolderName(sanitized);
    }

    private static string NormalizeProjectFolderName(string value)
    {
        string replaced = new(value.Trim().Select(ch => InvalidProjectFolderNameChars.Contains(ch) ? '_' : ch).ToArray());
        return replaced.Trim().TrimEnd('.', ' ');
    }

    private static string EscapeReservedProjectFolderName(string sanitized)
    {
        if (sanitized is "." or "..")
        {
            return $"{sanitized}_";
        }

        int extensionIndex = sanitized.IndexOf('.');
        string baseToken = extensionIndex < 0
            ? sanitized
            : sanitized[..extensionIndex];
        if (!ReservedProjectFolderNames.Contains(baseToken))
        {
            return sanitized;
        }

        return extensionIndex < 0
            ? $"{sanitized}_"
            : $"{sanitized[..extensionIndex]}_{sanitized[extensionIndex..]}";
    }
}
