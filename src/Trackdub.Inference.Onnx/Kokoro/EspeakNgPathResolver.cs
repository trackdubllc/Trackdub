namespace Trackdub.Inference.Onnx.Kokoro;

public static class EspeakNgPathResolver
{
    public const string EnvironmentVariableName = "TRACKDUB_ESPEAK_NG_PATH";

    private static readonly string ExecutableName =
        OperatingSystem.IsWindows() ? "espeak-ng.exe" : "espeak-ng";
    private const string PathCommandName = "espeak-ng";

    public static string Resolve(
        string? explicitPath = null,
        string? baseDirectory = null,
        string? workingDirectory = null,
        string? environmentVariableValue = null,
        string? pathEnvironmentValue = null)
    {
        string? configuredPath = ResolveConfiguredPath(explicitPath, "explicit eSpeak-NG path", pathEnvironmentValue);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        configuredPath = ResolveConfiguredPath(
            environmentVariableValue ?? Environment.GetEnvironmentVariable(EnvironmentVariableName),
            EnvironmentVariableName,
            pathEnvironmentValue);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        string appBaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        foreach (string candidate in GetBundledCandidates(appBaseDirectory))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string currentDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : workingDirectory;
        foreach (string candidate in GetDeveloperCandidates(currentDirectory))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (string commandName in new[] { ExecutableName, PathCommandName })
        {
            string? pathCandidate = FindOnPath(commandName, pathEnvironmentValue);
            if (!string.IsNullOrWhiteSpace(pathCandidate))
            {
                return pathCandidate;
            }
        }

        throw CreateUnavailableException(appBaseDirectory, currentDirectory);
    }

    public static IReadOnlyList<string> GetBundledCandidates(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string root = Path.GetFullPath(baseDirectory);
        string runtimeFolder = GetRuntimeFolder();
        return
        [
            Path.Combine(root, "tools", "espeak-ng", ExecutableName),
            Path.Combine(root, "runtimes", runtimeFolder, "native", "espeak-ng", ExecutableName),
            Path.Combine(root, "runtimes", runtimeFolder, "native", ExecutableName),
            Path.Combine(root, "espeak-ng", ExecutableName)
        ];
    }

    private static string GetRuntimeFolder()
    {
        bool arm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64;
        if (OperatingSystem.IsWindows()) return arm64 ? "win-arm64" : "win-x64";
        if (OperatingSystem.IsMacOS()) return arm64 ? "osx-arm64" : "osx-x64";
        return arm64 ? "linux-arm64" : "linux-x64";
    }

    public static IReadOnlyList<string> GetDeveloperCandidates(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in EnumerateDirectoryAndParents(workingDirectory))
        {
            foreach (string candidate in GetBundledCandidates(root))
            {
                if (seen.Add(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates;
    }

    internal static InvalidOperationException CreateUnavailableException(
        string? baseDirectory = null,
        string? workingDirectory = null,
        Exception? innerException = null)
    {
        string appBaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        string currentDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : workingDirectory;

        string exe = ExecutableName;
        string rid = GetRuntimeFolder();
        char s = Path.DirectorySeparatorChar;
        string message = string.Join(Environment.NewLine,
        [
            $"eSpeak-NG is required for Kokoro TTS phonemization, but Trackdub could not locate {exe}.",
            $"Set {EnvironmentVariableName} to the full path of {exe}, install eSpeak-NG so it is on PATH, or place {exe} under one of these locations:",
            $"- tools{s}espeak-ng{s}{exe}",
            $"- runtimes{s}{rid}{s}native{s}espeak-ng{s}{exe}",
            $"- runtimes{s}{rid}{s}native{s}{exe}",
            $"- espeak-ng{s}{exe}",
            $"App base checked: {Path.GetFullPath(appBaseDirectory)}",
            $"Working directory checked: {Path.GetFullPath(currentDirectory)}"
        ]);

        return new InvalidOperationException(message, innerException);
    }

    private static string? ResolveConfiguredPath(
        string? configuredPath,
        string sourceName,
        string? pathEnvironmentValue)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        string trimmed = configuredPath.Trim();
        if (File.Exists(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        bool looksLikePath = Path.IsPathFullyQualified(trimmed) ||
                             trimmed.Contains(Path.DirectorySeparatorChar) ||
                             trimmed.Contains(Path.AltDirectorySeparatorChar);
        if (!looksLikePath)
        {
            string? onPath = FindOnPath(trimmed, pathEnvironmentValue);
            if (!string.IsNullOrWhiteSpace(onPath))
            {
                return onPath;
            }
        }

        throw new InvalidOperationException(
            $"The {sourceName} value '{trimmed}' does not point to a usable eSpeak-NG executable. " +
            $"Set {EnvironmentVariableName} to the full path of {ExecutableName}, install eSpeak-NG so it is on PATH, or place {ExecutableName} in a supported bundled location.");
    }

    private static string? FindOnPath(string executableName, string? pathEnvironmentValue)
    {
        string? pathEnvironment = pathEnvironmentValue ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            return null;
        }

        foreach (string pathSegment in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(pathSegment.Trim(), executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDirectoryAndParents(string directory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }
}
