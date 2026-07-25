using Trackdub.Cli;
using Trackdub.Cli.Handlers;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class PresetHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PresetStore _store;
    private readonly StringWriter _stdout;
    private readonly StringWriter _stderr;

    public PresetHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trackdub-handler-tests-{Guid.NewGuid():N}");
        _store = new PresetStore(_tempDir);
        _stdout = new StringWriter();
        _stderr = new StringWriter();
    }

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();

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
            Version = 1,
            TargetLanguage = targetLanguage,
            SourceLanguage = sourceLanguage,
            Models = new Dictionary<string, string> { ["asr"] = "whisper-large-v3", ["tts"] = "kokoro-onnx" },
            ExportFormat = exportFormat,
            ExecutionProvider = executionProvider,
            DevicePolicy = devicePolicy,
            EnableAsrTextRefinement = enableAsrTextRefinement,
        };

    // ─── SaveAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ValidName_ReturnsSuccessAndEmitsConfirmation()
    {
        int exitCode = await PresetHandler.SaveAsync(
            "my-preset", CreatePreset(), _store, _stdout, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        string output = _stdout.ToString();
        Assert.Contains("Saved preset 'my-preset'", output);
        Assert.Contains(Path.Combine(_tempDir, "my-preset.json"), output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has!special")]
    [InlineData("way-too-long-name-that-exceeds-sixty-four-characters-aaaaaaaaaaaaa")]
    public async Task SaveAsync_InvalidName_ReturnsArgumentErrorAndEmitsValidationMessage(string invalidName)
    {
        int exitCode = await PresetHandler.SaveAsync(
            invalidName, CreatePreset(), _store, _stdout, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string output = _stdout.ToString();
        Assert.Contains("Invalid preset name", output);
        Assert.Contains("1-64 characters", output);
    }

    [Theory]
    [InlineData("windows-ml")]
    [InlineData("trt-rtx")]
    [InlineData("bogus")]
    public async Task SaveAsync_InvalidExecutionProvider_ReturnsArgumentErrorAndEmitsValidationMessage(string invalidProvider)
    {
        int exitCode = await PresetHandler.SaveAsync(
            "bad-ep", CreatePreset(executionProvider: invalidProvider), _store, _stdout, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string output = _stdout.ToString();
        Assert.Contains("Invalid execution provider", output);
        Assert.Contains(CliParseHelpers.FormatSupportedExecutionProviders(), output);
        Assert.False(File.Exists(Path.Combine(_tempDir, "bad-ep.json")));
    }

    [Theory]
    [InlineData("not-a-policy")]
    [InlineData("bogus")]
    public async Task SaveAsync_InvalidDevicePolicy_ReturnsArgumentErrorAndEmitsValidationMessage(string invalidPolicy)
    {
        int exitCode = await PresetHandler.SaveAsync(
            "bad-dp", CreatePreset(devicePolicy: invalidPolicy), _store, _stdout, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string output = _stdout.ToString();
        Assert.Contains("Invalid device policy", output);
        Assert.Contains(WindowsMlExecutionDevicePolicySettings.FormatSupportedKeys(), output);
        Assert.False(File.Exists(Path.Combine(_tempDir, "bad-dp.json")));
    }

    [Fact]
    public async Task SaveAsync_EmptyExecutionPreferences_AreTreatedAsDefaultAndAccepted()
    {
        int exitCode = await PresetHandler.SaveAsync(
            "default-prefs",
            CreatePreset(executionProvider: null, devicePolicy: null),
            _store, _stdout, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.True(File.Exists(Path.Combine(_tempDir, "default-prefs.json")));
    }

    [Fact]
    public async Task SaveAsync_Overwrite_ReturnsSuccessAndNewValuePersists()
    {
        var first = CreatePreset(targetLanguage: "es");
        var second = CreatePreset(targetLanguage: "fr");

        await PresetHandler.SaveAsync("overwrite", first, _store, _stdout, CancellationToken.None);
        int exitCode = await PresetHandler.SaveAsync("overwrite", second, _store, _stdout, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);

        var loaded = await _store.LoadAsync("overwrite", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("fr", loaded.TargetLanguage);
    }

    // ─── LoadAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_ExistingPreset_ReturnsSuccessAndEmitsKeyValues()
    {
        var preset = CreatePreset(
            targetLanguage: "de",
            sourceLanguage: "en",
            exportFormat: "mkv",
            executionProvider: "cpu",
            devicePolicy: "explicit",
            enableAsrTextRefinement: false);

        await _store.SaveAsync("load-test", preset, CancellationToken.None);

        int exitCode = await PresetHandler.LoadAsync(
            "load-test", _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        string output = _stdout.ToString();
        Assert.Contains("target-language: de", output);
        Assert.Contains("source-language: en", output);
        Assert.Contains("export-format: mkv", output);
        Assert.Contains("execution-provider: cpu", output);
        Assert.Contains("device-policy: explicit", output);
        Assert.Contains("enable-asr-text-refinement: false", output);
        Assert.Contains("models:", output);
        Assert.Contains("asr=whisper-large-v3", output);
    }

    [Fact]
    public async Task LoadAsync_NonExistentPreset_ReturnsArgumentErrorAndEmitsNotFound()
    {
        Directory.CreateDirectory(_tempDir);

        int exitCode = await PresetHandler.LoadAsync(
            "no-such-preset", _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string errorOutput = _stderr.ToString();
        Assert.Contains("Preset 'no-such-preset' not found.", errorOutput);
    }

    [Fact]
    public async Task LoadAsync_InvalidName_ReturnsArgumentErrorAndEmitsValidationMessage()
    {
        int exitCode = await PresetHandler.LoadAsync(
            "bad name!", _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string errorOutput = _stderr.ToString();
        Assert.Contains("Invalid preset name", errorOutput);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ReturnsArgumentErrorAndEmitsFailure()
    {
        Directory.CreateDirectory(_tempDir);
        string filePath = Path.Combine(_tempDir, "corrupt.json");
        await File.WriteAllTextAsync(filePath, "{ not valid json !!!");

        int exitCode = await PresetHandler.LoadAsync(
            "corrupt", _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string errorOutput = _stderr.ToString();
        Assert.Contains("Failed to read preset 'corrupt'", errorOutput);
    }

    [Fact]
    public async Task LoadAsync_NewerSchemaVersion_ReturnsArgumentErrorAndEmitsFailure()
    {
        Directory.CreateDirectory(_tempDir);

        int unsupportedVersion = PipelinePreset.CurrentVersion + 1;
        string json = $$"""
            {
              "version": {{unsupportedVersion}},
              "targetLanguage": "ja"
            }
            """;
        string filePath = Path.Combine(_tempDir, "too-new.json");
        await File.WriteAllTextAsync(filePath, json);

        int exitCode = await PresetHandler.LoadAsync(
            "too-new", _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string errorOutput = _stderr.ToString();
        Assert.Contains("Failed to read preset 'too-new'", errorOutput);
        Assert.Contains("unsupported schema version", errorOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_PresetWithOptionalFieldsNull_EmitsOnlyTargetLanguage()
    {
        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "ja",
            SourceLanguage = null,
            Models = null,
            ExportFormat = null,
            ExecutionProvider = null,
            DevicePolicy = null,
            EnableAsrTextRefinement = null,
        };

        await _store.SaveAsync("minimal", preset, CancellationToken.None);

        int exitCode = await PresetHandler.LoadAsync(
            "minimal", _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        string output = _stdout.ToString();
        Assert.Contains("target-language: ja", output);
        Assert.DoesNotContain("source-language:", output);
        Assert.DoesNotContain("models:", output);
        Assert.DoesNotContain("export-format:", output);
        Assert.DoesNotContain("execution-provider:", output);
        Assert.DoesNotContain("device-policy:", output);
        Assert.DoesNotContain("enable-asr-text-refinement:", output);
    }

    // ─── ListAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_EmptyStore_ReturnsSuccessAndEmitsNoPresetsMessage()
    {
        // Don't create the directory — simulates fresh install
        int exitCode = await PresetHandler.ListAsync(_store, _stdout, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        string output = _stdout.ToString();
        Assert.Contains("No presets saved.", output);
    }

    [Fact]
    public async Task ListAsync_PopulatedStore_ReturnsSuccessAndEmitsSortedNames()
    {
        await _store.SaveAsync("zulu", CreatePreset(), CancellationToken.None);
        await _store.SaveAsync("alpha", CreatePreset(), CancellationToken.None);
        await _store.SaveAsync("BRAVO", CreatePreset(), CancellationToken.None);

        int exitCode = await PresetHandler.ListAsync(_store, _stdout, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        string output = _stdout.ToString();

        // Verify sorted order (OrdinalIgnoreCase: alpha < BRAVO < zulu)
        int alphaPos = output.IndexOf("alpha", StringComparison.Ordinal);
        int bravoPos = output.IndexOf("BRAVO", StringComparison.Ordinal);
        int zuluPos = output.IndexOf("zulu", StringComparison.Ordinal);

        Assert.True(alphaPos >= 0, "alpha should appear in output");
        Assert.True(bravoPos >= 0, "BRAVO should appear in output");
        Assert.True(zuluPos >= 0, "zulu should appear in output");
        Assert.True(alphaPos < bravoPos, "alpha should appear before BRAVO");
        Assert.True(bravoPos < zuluPos, "BRAVO should appear before zulu");
    }

    // ─── DeleteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ExistingPreset_ReturnsSuccessAndEmitsConfirmation()
    {
        await _store.SaveAsync("doomed", CreatePreset(), CancellationToken.None);

        int exitCode = await PresetHandler.DeleteAsync(
            "doomed", _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitSuccess, exitCode);
        string output = _stdout.ToString();
        Assert.Contains("Deleted preset 'doomed'.", output);
        Assert.False(File.Exists(Path.Combine(_tempDir, "doomed.json")));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentPreset_ReturnsArgumentErrorAndEmitsNotFound()
    {
        Directory.CreateDirectory(_tempDir);

        int exitCode = await PresetHandler.DeleteAsync(
            "ghost", _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string errorOutput = _stderr.ToString();
        Assert.Contains("Preset 'ghost' not found.", errorOutput);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has@symbol")]
    public async Task DeleteAsync_InvalidName_ReturnsArgumentErrorAndEmitsValidationMessage(string invalidName)
    {
        int exitCode = await PresetHandler.DeleteAsync(
            invalidName, _store, _stdout, _stderr, CancellationToken.None);

        Assert.Equal(Program.ExitArgumentError, exitCode);
        string errorOutput = _stderr.ToString();
        Assert.Contains("Invalid preset name", errorOutput);
    }
}
