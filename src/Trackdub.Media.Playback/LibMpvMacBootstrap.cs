using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Trackdub.Media.Playback;

/// <summary>
/// One-time bootstrap that downloads libmpv into
/// ~/Library/Application Support/Trackdub/native/{rid}/ when the git-tracked
/// <c>runtime/win-native-deps.manifest.json</c> is present next to the app (same URLs as
/// <c>tools/dev/Fetch-MacNativeDeps.ps1</c>).
/// </summary>
public static class LibMpvMacBootstrap
{
    private const int DownloadBufferSize = 65536;
    private static readonly Lock Gate = new();
    private static bool Attempted;

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static void TryEnsureIfManifestPresent()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        lock (Gate)
        {
            if (Attempted)
            {
                return;
            }

            try
            {
                if (TryEnsureCore())
                {
                    Attempted = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Trace.TraceWarning($"LibMpv bootstrap failed: {ex}");
                Attempted = true;
            }
        }
    }

    private static bool TryEnsureCore()
    {
        string rid = ResolveMacRid();
        if (string.IsNullOrWhiteSpace(rid) ||
            Path.IsPathRooted(rid) ||
            rid.Contains(Path.DirectorySeparatorChar) ||
            rid.Contains(Path.AltDirectorySeparatorChar) ||
            rid.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string destinationDylib = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "Trackdub",
            "native",
            rid,
            "libmpv.2.dylib");

        if (File.Exists(destinationDylib))
        {
            return true;
        }

        WinNativeDepsManifestRoot? manifest = WinNativeDepsManifestLoader.TryLoadFromApplicationDirectory();
        if (manifest is null ||
            manifest.Runtimes is null ||
            !manifest.Runtimes.TryGetValue(rid, out WinNativeDepsRuntimeEntry? entry) ||
            string.IsNullOrWhiteSpace(entry.LibmpvDevArchiveUrl))
        {
            return false;
        }

        string member = string.IsNullOrWhiteSpace(entry.LibmpvExtractMember)
            ? "lib/libmpv.2.dylib"
            : entry.LibmpvExtractMember.Trim();

        string scratch = Path.Join(Path.GetTempPath(), "trackdub-libmpv-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        try
        {
            string archivePath = Path.Combine(scratch, "libmpv.tar.gz");
            DownloadToFile(entry.LibmpvDevArchiveUrl, archivePath);

            if (!string.IsNullOrWhiteSpace(entry.LibmpvDevArchiveSha256))
            {
                string actualDigest = ComputeSha256(archivePath);
                string expectedDigest = entry.LibmpvDevArchiveSha256.Trim();
                if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"libmpv archive integrity check failed. Expected SHA256: {expectedDigest}, Actual: {actualDigest}");
                }
            }

            string extractRoot = Path.Combine(scratch, "extract");
            Directory.CreateDirectory(extractRoot);
            ExtractTarGzWithSystemTar(archivePath, extractRoot);

            string extracted = Path.Combine(extractRoot, member.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(extracted))
            {
                extracted = FindLibMpvDylib(extractRoot) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(extracted) || !File.Exists(extracted))
            {
                return false;
            }

            string? destDir = Path.GetDirectoryName(destinationDylib);
            if (!string.IsNullOrWhiteSpace(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(extracted, destinationDylib, overwrite: true);
            return File.Exists(destinationDylib);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException ex)
            {
                Trace.TraceWarning($"Failed to delete temporary libmpv directory '{scratch}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceWarning($"Access denied deleting temporary libmpv directory '{scratch}': {ex.Message}");
            }
        }
    }

    private static void ExtractTarGzWithSystemTar(string archivePath, string destinationDirectory)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "tar",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-xzf");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(destinationDirectory);

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start tar for libmpv extraction.");

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"tar exited with code {process.ExitCode} while extracting libmpv.");
        }
    }

    private static string? FindLibMpvDylib(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "libmpv*.dylib", SearchOption.AllDirectories))
        {
            return path;
        }

        return null;
    }

    private static string ResolveMacRid() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

    private static void DownloadToFile(string uri, string destinationPath)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using HttpResponseMessage response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using Stream networkStream = response.Content.ReadAsStream();
        using var fileStream = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize);

        networkStream.CopyTo(fileStream);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Trackdub/1.0");
        return client;
    }

    private static string ComputeSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
