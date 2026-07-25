using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Trackdub.Media.Playback;

/// <summary>
/// One-time bootstrap that downloads libmpv into %LocalAppData%\Trackdub\native\{rid}\ when the
/// git-tracked <c>runtime/win-native-deps.manifest.json</c> is present next to the app (same URLs as
/// <c>tools/dev/Fetch-WinNativeDeps.ps1</c>).
/// </summary>
public static class LibMpvWindowsBootstrap
{
    private const int DownloadBufferSize = 65536;
    private static readonly Lock Gate = new();
    private static bool Attempted;

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static void TryEnsureIfManifestPresent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            if (Attempted)
            {
                return;
            }

            Attempted = true;
        }

        try
        {
            TryEnsureCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Async variant of <see cref="TryEnsureIfManifestPresent"/> for non-blocking bootstrap.
    /// </summary>
    public static async Task TryEnsureIfManifestPresentAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            if (Attempted)
            {
                return;
            }

            Attempted = true;
        }

        try
        {
            await TryEnsureCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task TryEnsureCoreAsync(CancellationToken cancellationToken)
    {
        string rid = ResolveWindowsRid();
        string destinationDll = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub",
            "native",
            rid,
            "libmpv-2.dll");

        if (File.Exists(destinationDll))
        {
            return;
        }

        WinNativeDepsManifestRoot? manifest = WinNativeDepsManifestLoader.TryLoadFromApplicationDirectory();
        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.SevenZipPortableExeUrl) ||
            manifest.Runtimes is null ||
            !manifest.Runtimes.TryGetValue(rid, out WinNativeDepsRuntimeEntry? entry) ||
            string.IsNullOrWhiteSpace(entry.LibmpvDevArchiveUrl))
        {
            return;
        }

        string member = string.IsNullOrWhiteSpace(entry.LibmpvExtractMember)
            ? "libmpv-2.dll"
            : entry.LibmpvExtractMember.Trim();

        string scratch = Path.Combine(Path.GetTempPath(), "trackdub-libmpv-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        try
        {
            string sevenZip = Path.Combine(scratch, "7zr.exe");
            string archivePath = Path.Combine(scratch, "mpv-dev.7z");
            await DownloadToFileAsync(manifest.SevenZipPortableExeUrl, sevenZip, cancellationToken).ConfigureAwait(false);
            await DownloadToFileAsync(entry.LibmpvDevArchiveUrl, archivePath, cancellationToken).ConfigureAwait(false);

            ProcessStartInfo psi = new()
            {
                FileName = sevenZip,
                WorkingDirectory = scratch,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "x", archivePath, member, "-y" },
            };

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start 7zr for libmpv extraction.");

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return;
            }

            string extracted = Path.Combine(scratch, Path.GetFileName(member));
            if (!File.Exists(extracted))
            {
                return;
            }

            string? destDir = Path.GetDirectoryName(destinationDll);
            if (!string.IsNullOrWhiteSpace(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(extracted, destinationDll, overwrite: true);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ResolveWindowsRid() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    private static async Task DownloadToFileAsync(string uri, string destinationPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using Stream networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var fileStream = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            useAsync: true);

        await networkStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    private static void DownloadToFile(string uri, string destinationPath)
    {
        DownloadToFileAsync(uri, destinationPath, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Trackdub/1.0");
        return client;
    }
}
