using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Kokoro;

namespace Trackdub.Inference.Tests;

public sealed class KokoroVoiceCatalogTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    public void Dispose()
    {
        foreach (string dir in tempDirectories)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_NoVoicesDirectory_ReturnsEmptyCatalog()
    {
        string root = CreateTempModelRoot();

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Empty(catalog.GetVoices());
    }

    [Fact]
    public async Task Load_EmptyVoicesDirectory_ReturnsEmptyCatalog()
    {
        string root = CreateTempModelRoot();
        Directory.CreateDirectory(Path.Combine(root, "voices"));

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Empty(catalog.GetVoices());
    }

    [Fact]
    public async Task Load_ValidBinFiles_ParsesEntries()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "af_heart");
        CreateFakeVoicepackBin(root, "am_adam");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Equal(2, catalog.GetVoices().Count);
    }

    [Fact]
    public async Task Load_SkipsFilesWithInvalidNamingFormat()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "invalid");      // too short
        CreateFakeVoicepackBin(root, "ab");            // too short (< 3 chars)
        CreateFakeVoicepackBin(root, "abXhello");      // underscore not at index 2
        CreateFakeVoicepackBin(root, "af_heart");      // valid

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Single(catalog.GetVoices());
        Assert.Equal("af_heart", catalog.GetVoices()[0].VoiceId);
    }

    [Fact]
    public async Task Load_NonBinFilesAreIgnored()
    {
        string root = CreateTempModelRoot();
        string voicesDir = Path.Combine(root, "voices");
        Directory.CreateDirectory(voicesDir);
        File.WriteAllBytes(Path.Combine(voicesDir, "af_heart.json"), []);
        File.WriteAllBytes(Path.Combine(voicesDir, "am_adam.txt"), []);
        CreateFakeVoicepackBin(root, "bf_alice");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Single(catalog.GetVoices());
    }

    [Fact]
    public async Task Load_VoicesAreSortedByVoiceId()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "zm_zhang");
        CreateFakeVoicepackBin(root, "af_heart");
        CreateFakeVoicepackBin(root, "bm_george");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        IReadOnlyList<VoiceCatalogEntry> voices = catalog.GetVoices();
        Assert.Equal("af_heart", voices[0].VoiceId);
        Assert.Equal("bm_george", voices[1].VoiceId);
        Assert.Equal("zm_zhang", voices[2].VoiceId);
    }

    // ── Locale prefix parsing ─────────────────────────────────────────────────

    [Theory]
    [InlineData("af_heart", "en-us")]
    [InlineData("bf_alice", "en-gb")]
    [InlineData("ef_rosa", "es")]
    [InlineData("ff_camille", "fr")]
    [InlineData("hf_ananya", "hi")]
    [InlineData("if_lucia", "it")]
    [InlineData("jf_hana", "ja")]
    [InlineData("kf_mina", "ko")]
    [InlineData("pf_ana", "pt")]
    [InlineData("rf_daria", "ru")]
    [InlineData("zf_xiaoyi", "zh")]
    public async Task Load_ParsesLocalePrefix(string voiceId, string expectedLanguageCode)
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, voiceId);

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        VoiceCatalogEntry entry = Assert.Single(catalog.GetVoices());
        Assert.Equal(expectedLanguageCode, entry.LanguageCode);
    }

    [Fact]
    public async Task Load_UnknownLocalePrefix_MapsToUnknown()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "xf_mystery");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        VoiceCatalogEntry entry = Assert.Single(catalog.GetVoices());
        Assert.Equal("unknown", entry.LanguageCode);
    }

    // ── Gender prefix parsing ─────────────────────────────────────────────────

    [Theory]
    [InlineData("af_heart", "female")]
    [InlineData("am_adam", "male")]
    public async Task Load_ParsesGenderFromSecondChar(string voiceId, string expectedGender)
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, voiceId);

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        VoiceCatalogEntry entry = Assert.Single(catalog.GetVoices());
        Assert.Equal(expectedGender, entry.Gender);
    }

    [Fact]
    public async Task Load_UnknownGenderChar_MapsToUnknown()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "ax_mystery");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        VoiceCatalogEntry entry = Assert.Single(catalog.GetVoices());
        Assert.Equal("unknown", entry.Gender);
    }

    // ── DisplayName parsing ───────────────────────────────────────────────────

    [Theory]
    [InlineData("af_heart", "Heart")]
    [InlineData("am_adam", "Adam")]
    [InlineData("bm_george", "George")]
    [InlineData("af_sky_blue", "Sky Blue")]   // underscores become spaces, title-cased
    public async Task Load_ParsesDisplayName(string voiceId, string expectedDisplayName)
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, voiceId);

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        VoiceCatalogEntry entry = Assert.Single(catalog.GetVoices());
        Assert.Equal(expectedDisplayName, entry.DisplayName);
    }

    // ── GetVoices ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetVoices_NoLanguageFilter_ReturnsAll()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "af_heart");
        CreateFakeVoicepackBin(root, "bf_alice");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Equal(2, catalog.GetVoices().Count);
    }

    [Fact]
    public async Task GetVoices_NullLanguageCode_ReturnsAll()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "af_heart");
        CreateFakeVoicepackBin(root, "bf_alice");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Equal(2, catalog.GetVoices(null).Count);
    }

    [Fact]
    public async Task GetVoices_FiltersByLanguageCode()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "af_heart");   // en-us
        CreateFakeVoicepackBin(root, "am_adam");    // en-us
        CreateFakeVoicepackBin(root, "bf_alice");   // en-gb

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        IReadOnlyList<VoiceCatalogEntry> enUs = catalog.GetVoices("en-us");
        Assert.Equal(2, enUs.Count);
        Assert.All(enUs, v => Assert.Equal("en-us", v.LanguageCode));
    }

    [Fact]
    public async Task GetVoices_LanguageWithNoMatches_ReturnsEmpty()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "af_heart");   // en-us

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Empty(catalog.GetVoices("zh"));
    }

    // ── TryGetVoice ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetVoice_KnownVoiceId_ReturnsTrueAndEntry()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "af_heart");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        bool found = catalog.TryGetVoice("af_heart", out VoiceCatalogEntry? entry);

        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal("af_heart", entry.VoiceId);
    }

    [Fact]
    public async Task TryGetVoice_UnknownVoiceId_ReturnsFalse()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "af_heart");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        bool found = catalog.TryGetVoice("xx_nonexistent", out VoiceCatalogEntry? entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    [Fact]
    public async Task TryGetVoice_EmptyCatalog_ReturnsFalse()
    {
        string root = CreateTempModelRoot();

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        bool found = catalog.TryGetVoice("af_heart", out VoiceCatalogEntry? entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    // ── Voicepack paths ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetBinPath_ReturnsNull_WhenBinFileDoesNotExist()
    {
        string root = CreateTempModelRoot();

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        Assert.Null(catalog.GetBinPath("af_ghost"));
    }

    [Fact]
    public async Task GetBinPath_ReturnsPath_WhenBinFileExists()
    {
        string root = CreateTempModelRoot();
        CreateFakeVoicepackBin(root, "af_heart");

        KokoroVoiceCatalog catalog = await KokoroVoiceCatalog.LoadAsync(root);

        string? binPath = catalog.GetBinPath("af_heart");

        Assert.NotNull(binPath);
        Assert.True(File.Exists(binPath));
        Assert.EndsWith("af_heart.bin", binPath, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateTempModelRoot()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.KokoroVoiceCatalogTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirectories.Add(dir);
        return dir;
    }

    private static void CreateFakeVoicepackBin(string modelRoot, string voiceId)
    {
        string voicesDir = Path.Combine(modelRoot, "voices");
        Directory.CreateDirectory(voicesDir);
        File.WriteAllBytes(Path.Combine(voicesDir, $"{voiceId}.bin"), []);
    }
}
