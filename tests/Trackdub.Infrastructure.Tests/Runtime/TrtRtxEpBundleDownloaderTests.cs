using System.IO.Compression;
using System.Text;
using Trackdub.Infrastructure.Runtime.TrtRtxEp;

namespace Trackdub.Infrastructure.Tests.Runtime;

public sealed class TrtRtxEpBundleDownloaderTests
{
    private const int UnixSymlinkAttributes = unchecked((int)0xA1FF0000);

    [Fact]
    public void ExtractZipPreservingSymlinks_resolves_unix_soname_chains_instead_of_writing_link_text()
    {
        string root = Path.Join(Path.GetTempPath(), $"trackdub-trt-zip-{Guid.NewGuid():N}");
        string archivePath = Path.Join(root, "bundle.zip");
        string extractDirectory = Path.Join(root, "extract");
        Directory.CreateDirectory(root);

        try
        {
            // Mirrors TensorRT-RTX-EP-ABI-v0.4.0-cu13-linux-x86_64.zip: nested folder, SONAME symlinks.
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                AddFile(archive, "bundle/libtensorrt_rtx.so.1.6.1", "real-runtime");
                AddSymlink(archive, "bundle/libtensorrt_rtx.so", "libtensorrt_rtx.so.1");
                AddSymlink(archive, "bundle/libtensorrt_rtx.so.1", "libtensorrt_rtx.so.1.6.1");
            }

            TrtRtxEpBundleDownloader.ExtractZipPreservingSymlinks(archivePath, extractDirectory);

            string bundle = Path.Join(extractDirectory, "bundle");
            Assert.Equal("real-runtime", File.ReadAllText(Path.Join(bundle, "libtensorrt_rtx.so")));
            Assert.Equal("real-runtime", File.ReadAllText(Path.Join(bundle, "libtensorrt_rtx.so.1")));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal("libtensorrt_rtx.so.1", new FileInfo(Path.Join(bundle, "libtensorrt_rtx.so")).LinkTarget);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../evil.so")]
    [InlineData("/usr/lib/libcudart.so.13")]
    public void ExtractZipPreservingSymlinks_rejects_link_targets_outside_the_bundle(string target)
    {
        string root = Path.Join(Path.GetTempPath(), $"trackdub-trt-zip-{Guid.NewGuid():N}");
        string archivePath = Path.Join(root, "bundle.zip");
        Directory.CreateDirectory(root);

        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                AddSymlink(archive, "libcudart.so", target);
            }

            Assert.Throws<InvalidOperationException>(() =>
                TrtRtxEpBundleDownloader.ExtractZipPreservingSymlinks(archivePath, Path.Join(root, "extract")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExtractZipPreservingSymlinks_rejects_entries_escaping_the_extract_directory()
    {
        string root = Path.Join(Path.GetTempPath(), $"trackdub-trt-zip-{Guid.NewGuid():N}");
        string archivePath = Path.Join(root, "bundle.zip");
        Directory.CreateDirectory(root);

        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                AddFile(archive, "../escape.dll", "x");
            }

            Assert.Throws<InvalidOperationException>(() =>
                TrtRtxEpBundleDownloader.ExtractZipPreservingSymlinks(archivePath, Path.Join(root, "extract")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AddFile(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using Stream stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static void AddSymlink(ZipArchive archive, string name, string target)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        entry.ExternalAttributes = UnixSymlinkAttributes;
        using Stream stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(target));
    }
}
