using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class BatchFileDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public BatchFileDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trackdub-batch-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void CreateFile(string relativePath)
    {
        string fullPath = Path.Combine(_tempDir, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(fullPath, []);
    }

    // ─── FromDirectory: extension filtering ─────────────────────────────────────

    [Fact]
    public void FromDirectory_FindsOnlySupportedExtensions()
    {
        CreateFile("video.mp4");
        CreateFile("audio.wav");
        CreateFile("movie.mkv");
        CreateFile("clip.mov");
        CreateFile("stream.webm");
        CreateFile("song.flac");
        CreateFile("track.mp3");
        CreateFile("document.txt");
        CreateFile("report.pdf");
        CreateFile("image.png");

        var result = BatchFileDiscovery.FromDirectory(_tempDir, recursive: false);

        Assert.Equal(7, result.Count);
        Assert.All(result, f =>
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            Assert.Contains(ext, new[] { ".mp4", ".mkv", ".mov", ".webm", ".wav", ".flac", ".mp3" });
        });
    }

    [Fact]
    public void FromDirectory_ExtensionMatchingIsCaseInsensitive()
    {
        CreateFile("upper.MP4");
        CreateFile("mixed.WaV");
        CreateFile("lower.mkv");

        var result = BatchFileDiscovery.FromDirectory(_tempDir, recursive: false);

        Assert.Equal(3, result.Count);
    }

    // ─── FromDirectory: sort order ──────────────────────────────────────────────

    [Fact]
    public void FromDirectory_SortsByFileNameOrdinalIgnoreCase()
    {
        CreateFile("Charlie.mp4");
        CreateFile("alpha.wav");
        CreateFile("BRAVO.mkv");

        var result = BatchFileDiscovery.FromDirectory(_tempDir, recursive: false);

        Assert.Equal(3, result.Count);
        Assert.Equal("alpha.wav", Path.GetFileName(result[0]));
        Assert.Equal("BRAVO.mkv", Path.GetFileName(result[1]));
        Assert.Equal("Charlie.mp4", Path.GetFileName(result[2]));
    }

    // ─── FromDirectory: non-recursive (default) ─────────────────────────────────

    [Fact]
    public void FromDirectory_NonRecursive_DoesNotDescendSubdirectories()
    {
        CreateFile("top.mp4");
        CreateFile("sub/nested.mp4");

        var result = BatchFileDiscovery.FromDirectory(_tempDir, recursive: false);

        Assert.Single(result);
        Assert.Equal("top.mp4", Path.GetFileName(result[0]));
    }

    // ─── FromDirectory: recursive ───────────────────────────────────────────────

    [Fact]
    public void FromDirectory_Recursive_DescendsSubdirectories()
    {
        CreateFile("top.mp4");
        CreateFile("sub/nested.mp4");
        CreateFile("sub/deep/deeper.wav");

        var result = BatchFileDiscovery.FromDirectory(_tempDir, recursive: true);

        Assert.Equal(3, result.Count);
    }

    // ─── FromDirectory: missing directory ────────────────────────────────────────

    [Fact]
    public void FromDirectory_ThrowsDirectoryNotFoundException_WhenPathMissing()
    {
        string nonExistent = Path.Combine(_tempDir, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() =>
            BatchFileDiscovery.FromDirectory(nonExistent, recursive: false));
    }

    // ─── FromDirectory: max batch size ──────────────────────────────────────────

    [Fact]
    public void ValidateBatchSize_ThrowsInvalidOperationException_WhenExceedsMaxBatchSize()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BatchFileDiscovery.ValidateBatchSize(10_001));

        Assert.Contains("10000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("10001", ex.Message, StringComparison.Ordinal);
    }

    // ─── FromDirectory: empty directory ─────────────────────────────────────────

    [Fact]
    public void FromDirectory_EmptyDirectory_ReturnsEmptyList()
    {
        var result = BatchFileDiscovery.FromDirectory(_tempDir, recursive: false);

        Assert.Empty(result);
    }

    [Fact]
    public void FromDirectory_DirectoryWithOnlyUnsupportedFiles_ReturnsEmptyList()
    {
        CreateFile("readme.txt");
        CreateFile("notes.pdf");
        CreateFile("data.json");

        var result = BatchFileDiscovery.FromDirectory(_tempDir, recursive: false);

        Assert.Empty(result);
    }

    // ─── FromGlob: pattern expansion and extension filtering ────────────────────

    [Fact]
    public void FromGlob_ExpandsPatternAndFiltersToSupportedExtensions()
    {
        CreateFile("videos/clip1.mp4");
        CreateFile("videos/clip2.mkv");
        CreateFile("videos/readme.txt");
        CreateFile("audio/song.mp3");

        var result = BatchFileDiscovery.FromGlob("videos/*", _tempDir);

        Assert.Equal(2, result.Count);
        Assert.All(result, f => Assert.Contains("videos", f));
    }

    [Fact]
    public void FromGlob_RecursiveGlob_FindsNestedFiles()
    {
        CreateFile("media/a/video.mp4");
        CreateFile("media/b/audio.wav");
        CreateFile("media/c/doc.txt");

        var result = BatchFileDiscovery.FromGlob("media/**/*", _tempDir);

        Assert.Equal(2, result.Count);
    }

    // ─── FromGlob: sort order ───────────────────────────────────────────────────

    [Fact]
    public void FromGlob_SortsByFullPathOrdinalIgnoreCase()
    {
        CreateFile("b-folder/second.mp4");
        CreateFile("a-folder/first.mp4");
        CreateFile("c-folder/third.mp4");

        var result = BatchFileDiscovery.FromGlob("**/*.mp4", _tempDir);

        Assert.Equal(3, result.Count);
        // OrdinalIgnoreCase sort by full path means a-folder < b-folder < c-folder
        Assert.Contains("a-folder", result[0]);
        Assert.Contains("b-folder", result[1]);
        Assert.Contains("c-folder", result[2]);
    }

    // ─── FromGlob: no matching files ────────────────────────────────────────────

    [Fact]
    public void FromGlob_NoMatchingMediaFiles_ReturnsEmptyList()
    {
        CreateFile("docs/readme.txt");
        CreateFile("docs/notes.pdf");

        var result = BatchFileDiscovery.FromGlob("docs/*", _tempDir);

        Assert.Empty(result);
    }

    [Fact]
    public void FromGlob_PatternMatchesNothing_ReturnsEmptyList()
    {
        CreateFile("videos/clip.mp4");

        var result = BatchFileDiscovery.FromGlob("nonexistent/**/*", _tempDir);

        Assert.Empty(result);
    }

    // ─── FromGlob: base directory missing ───────────────────────────────────────

    [Fact]
    public void FromGlob_ThrowsDirectoryNotFoundException_WhenBaseDirectoryMissing()
    {
        string nonExistent = Path.Combine(_tempDir, "no-such-dir");

        Assert.Throws<DirectoryNotFoundException>(() =>
            BatchFileDiscovery.FromGlob("**/*.mp4", nonExistent));
    }
}
