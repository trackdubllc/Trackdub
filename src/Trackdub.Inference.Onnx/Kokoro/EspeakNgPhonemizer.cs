using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Onnx.Kokoro;

public sealed partial class EspeakNgPhonemizer(string? configuredExecutablePath = null) : IGraphemeToPhoneme
{
    private const int StatusDllNotFoundExitCode = unchecked((int)0xC0000135);
    private const string EspeakDataPathVariableName = "ESPEAK_DATA_PATH";

    private string? resolvedExecutablePath;

    // Defer path resolution until Phonemize is actually invoked. Resolving
    // in the constructor would throw at workspace creation when eSpeak-NG
    // is missing, blocking unrelated features (ASR, translation, diarization,
    // playback) instead of just TTS.

    public string Phonemize(string text, string languageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        if (!LanguageCodePattern().IsMatch(languageCode))
        {
            throw new ArgumentException(
                "Language code may only contain letters, digits, underscores, and hyphens.",
                nameof(languageCode));
        }

        string executablePath = resolvedExecutablePath ??= EspeakNgPathResolver.Resolve(configuredExecutablePath);

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8
        };
        string? executableDirectory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            psi.WorkingDirectory = executableDirectory;
        }

        // The standalone Windows eSpeak-NG build has no valid compiled-in data path and
        // access-violates (0xC0000005) on startup unless ESPEAK_DATA_PATH points at the
        // directory containing espeak-ng-data. Honor a caller/environment-provided value;
        // otherwise wire the bundled data folder sitting next to the executable.
        if (!psi.Environment.ContainsKey(EspeakDataPathVariableName) &&
            TryGetBundledEspeakDataDirectory(executablePath) is { } bundledDataDirectory)
        {
            psi.Environment[EspeakDataPathVariableName] = bundledDataDirectory;
        }

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add(MapToEspeakVoiceName(languageCode));
        psi.ArgumentList.Add("--ipa=3");
        psi.ArgumentList.Add("-q");

        using Process process = StartProcess(psi);

        // Start reading stdout asynchronously before writing to stdin to avoid potential pipe deadlock.
        // If the process fills its stdout buffer before we start reading, it would block on write.
        Task<string> readTask = process.StandardOutput.ReadToEndAsync();

        process.StandardInput.Write(text.Trim());
        process.StandardInput.Close();

        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"espeak-ng did not complete within 10 seconds for text of {text.Length} chars.");
        }

        string output = readTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw CreateExitException(process.ExitCode, executablePath);
        }

        return NormalizeIpaOutput(output);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex LanguageCodePattern();

    private static Process StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw EspeakNgPathResolver.CreateUnavailableException(innerException: null);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or UnauthorizedAccessException)
        {
            throw EspeakNgPathResolver.CreateUnavailableException(innerException: ex);
        }
    }

    internal static InvalidOperationException CreateExitException(int exitCode, string executablePath)
    {
        if (exitCode is StatusDllNotFoundExitCode)
        {
            return new InvalidOperationException(
                "eSpeak-NG started but Windows could not load one of its dependent DLLs. " +
                "Copy the full eSpeak-NG runtime folder next to espeak-ng.exe, including its DLLs and espeak-ng-data directory. " +
                $"Current executable path: {executablePath}");
        }

        return new InvalidOperationException($"espeak-ng exited with code {exitCode}.");
    }

    /// <summary>
    /// Returns the directory eSpeak-NG should use as ESPEAK_DATA_PATH (the directory that
    /// contains the bundled <c>espeak-ng-data</c> folder), or null when no bundled data sits
    /// next to the executable.
    /// </summary>
    internal static string? TryGetBundledEspeakDataDirectory(string executablePath)
    {
        string? executableDirectory = Path.GetDirectoryName(executablePath);
        return !string.IsNullOrWhiteSpace(executableDirectory) &&
               Directory.Exists(Path.Combine(executableDirectory, "espeak-ng-data"))
            ? executableDirectory
            : null;
    }

    // eSpeak-NG ships "en" (US-accented) and "en-gb" but not "en-us" as a voice name.
    private static string MapToEspeakVoiceName(string languageCode) =>
        languageCode.Equals("en-us", StringComparison.OrdinalIgnoreCase) ? "en" : languageCode;

    private static string NormalizeIpaOutput(string raw) =>
        raw.Replace("\r\n", " ").Replace('\n', ' ').Replace('_', ' ').Trim();
}
