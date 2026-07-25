using System.Text;
using System.Text.Json;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class PresetStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PresetStore _store;

    public PresetStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trackdub-preset-tests-{Guid.NewGuid():N}");
        _store = new PresetStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static PipelinePreset CreatePreset(
        string targetLanguage = "es",
        string? sourceLanguage = "en",
        string? exportFormat = "mp4",
        string? executionProvider = "directml",
        string? devicePolicy = "max-performance",
        bool? enableAsrTextRefinement = true) => new()
        {
            Version = PipelinePreset.CurrentVersion,
            TargetLanguage = targetLanguage,
            SourceLanguage = sourceLanguage,
            Models = new Dictionary<string, string> { ["asr"] = "whisper-large-v3", ["tts"] = "kokoro-onnx" },
            ExportFormat = exportFormat,
            ExecutionProvider = executionProvider,
            DevicePolicy = devicePolicy,
            EnableAsrTextRefinement = enableAsrTextRefinement,
        };

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesAllFields()
    {
        var preset = CreatePreset();

        await _store.SaveAsync("test-round-trip", preset, CancellationToken.None);
        var loaded = await _store.LoadAsync("test-round-trip", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("es", loaded.TargetLanguage);
        Assert.Equal("en", loaded.SourceLanguage);
        Assert.Equal("mp4", loaded.ExportFormat);
        Assert.Equal("directml", loaded.ExecutionProvider);
        Assert.Equal("max-performance", loaded.DevicePolicy);
        Assert.True(loaded.EnableAsrTextRefinement);
        Assert.NotNull(loaded.Models);
        Assert.Equal("whisper-large-v3", loaded.Models["asr"]);
        Assert.Equal("kokoro-onnx", loaded.Models["tts"]);
    }

    [Fact]
    public async Task Save_Overwrite_SecondValuePersists()
    {
        var first = CreatePreset(targetLanguage: "es");
        var second = CreatePreset(targetLanguage: "fr");

        await _store.SaveAsync("overwrite-test", first, CancellationToken.None);
        await _store.SaveAsync("overwrite-test", second, CancellationToken.None);

        var loaded = await _store.LoadAsync("overwrite-test", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("fr", loaded.TargetLanguage);
    }

    [Fact]
    public async Task List_MultiplePresets_ReturnsSortedAlphabetically()
    {
        await _store.SaveAsync("charlie", CreatePreset(), CancellationToken.None);
        await _store.SaveAsync("alpha", CreatePreset(), CancellationToken.None);
        await _store.SaveAsync("BRAVO", CreatePreset(), CancellationToken.None);

        var names = await _store.ListAsync(CancellationToken.None);

        Assert.Equal(3, names.Count);
        Assert.Equal("alpha", names[0]);
        Assert.Equal("BRAVO", names[1]);
        Assert.Equal("charlie", names[2]);
    }

    [Fact]
    public async Task List_DirectoryDoesNotExist_ReturnsEmptyList()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}");
        var store = new PresetStore(nonExistentDir);

        var names = await store.ListAsync(CancellationToken.None);

        Assert.Empty(names);
    }

    [Fact]
    public async Task List_MalformedJsonFile_SkippedOthersReturned()
    {
        // Save a valid preset
        await _store.SaveAsync("valid-one", CreatePreset(), CancellationToken.None);

        // Write a malformed JSON file
        var malformedPath = Path.Combine(_tempDir, "broken.json");
        await File.WriteAllTextAsync(malformedPath, "{ not valid json at all !!!");

        var names = await _store.ListAsync(CancellationToken.None);

        Assert.Single(names);
        Assert.Equal("valid-one", names[0]);
    }

    [Fact]
    public async Task Delete_ExistingPreset_ReturnsTrueAndFileGone()
    {
        await _store.SaveAsync("to-delete", CreatePreset(), CancellationToken.None);

        bool result = await _store.DeleteAsync("to-delete", CancellationToken.None);

        Assert.True(result);
        Assert.False(File.Exists(Path.Combine(_tempDir, "to-delete.json")));
    }

    [Fact]
    public async Task Delete_MissingPreset_ReturnsFalse()
    {
        // Ensure directory exists but preset does not
        Directory.CreateDirectory(_tempDir);

        bool result = await _store.DeleteAsync("nonexistent", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task Save_CreatesDirectoryIfMissing()
    {
        var nestedDir = Path.Combine(Path.GetTempPath(), $"trackdub-nested-{Guid.NewGuid():N}", "presets");
        var store = new PresetStore(nestedDir);

        try
        {
            await store.SaveAsync("new-preset", CreatePreset(), CancellationToken.None);

            Assert.True(Directory.Exists(nestedDir));
            Assert.True(File.Exists(Path.Combine(nestedDir, "new-preset.json")));
        }
        finally
        {
            var root = Path.GetDirectoryName(nestedDir)!;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_FileContent_Utf8NoBom_CamelCase_TwoSpaceIndent()
    {
        var preset = CreatePreset(
            targetLanguage: "de",
            sourceLanguage: null,
            exportFormat: null,
            executionProvider: null,
            devicePolicy: null,
            enableAsrTextRefinement: null);

        await _store.SaveAsync("format-check", preset, CancellationToken.None);

        string filePath = Path.Combine(_tempDir, "format-check.json");
        byte[] rawBytes = await File.ReadAllBytesAsync(filePath);

        // UTF-8 no BOM: first bytes should NOT be EF BB BF
        Assert.False(
            rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF,
            "File should not have UTF-8 BOM");

        string content = Encoding.UTF8.GetString(rawBytes);

        // camelCase keys
        Assert.Contains("\"version\"", content);
        Assert.Contains("\"targetLanguage\"", content);

        // 2-space indent (check indentation pattern)
        Assert.Contains("\n  \"", content);

        // Null/default optional fields omitted (WhenWritingNull)
        Assert.DoesNotContain("\"sourceLanguage\"", content);
        Assert.DoesNotContain("\"exportFormat\"", content);
    }

    [Fact]
    public async Task Load_NonExistentPreset_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);

        var result = await _store.LoadAsync("does-not-exist", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Load_ToleratesExtraJsonFields_ForwardCompatibility()
    {
        Directory.CreateDirectory(_tempDir);

        // Write JSON with extra unknown fields
        string json = """
            {
              "version": 1,
              "targetLanguage": "ja",
              "futureField": "some-value",
              "anotherUnknown": 42
            }
            """;
        string filePath = Path.Combine(_tempDir, "forward-compat.json");
        await File.WriteAllTextAsync(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var loaded = await _store.LoadAsync("forward-compat", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("ja", loaded.TargetLanguage);
    }

    [Fact]
    public async Task Load_NewerSchemaVersion_ThrowsInvalidOperationException()
    {
        Directory.CreateDirectory(_tempDir);

        // Write a preset whose schema version is newer than supported.
        int unsupportedVersion = PipelinePreset.CurrentVersion + 1;
        string json = $$"""
            {
              "version": {{unsupportedVersion}},
              "targetLanguage": "ja"
            }
            """;
        string filePath = Path.Combine(_tempDir, "too-new.json");
        await File.WriteAllTextAsync(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.LoadAsync("too-new", CancellationToken.None));

        Assert.Contains("unsupported schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(unsupportedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
    }
}
