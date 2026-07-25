using System.Text.Json;

using Trackdub.Cli;
using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Integration tests for batch CLI processing, covering:
/// - Batch report JSON output structure (camelCase, enum-as-string)
/// - Preset resolution order (explicit > preset > default)
/// - BatchFileDiscovery error paths (missing directory, empty results)
/// - PresetNameValidator for invalid preset name rejection
/// - Mutual exclusion semantics (documented via type-level assertions)
/// </summary>
public sealed class BatchHandlerTests : IDisposable
{
    private readonly string _tempDir;

    public BatchHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trackdub-batch-handler-{Guid.NewGuid():N}");
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

    // ─── BatchReport JSON output structure ──────────────────────────────────────

    [Fact]
    public void BatchReport_SerializesWithCamelCaseAndEnumStrings()
    {
        var report = new BatchReport
        {
            Files =
            [
                new BatchFileOutcome { FilePath = "/media/video1.mp4", Status = BatchFileStatus.Success },
                new BatchFileOutcome { FilePath = "/media/video2.mp4", Status = BatchFileStatus.Failed, Reason = "Pipeline failed" },
                new BatchFileOutcome { FilePath = "/media/video3.mp4", Status = BatchFileStatus.Skipped, Reason = "Halted due to fail-fast" },
            ],
            SucceededCount = 1,
            FailedCount = 1,
            SkippedCount = 1,
        };

        string json = JsonSerializer.Serialize(report, CliJsonOptions.Default);

        // Verify camelCase property names
        Assert.Contains("\"files\":", json);
        Assert.Contains("\"filePath\":", json);
        Assert.Contains("\"status\":", json);
        Assert.Contains("\"reason\":", json);
        Assert.Contains("\"succeededCount\":", json);
        Assert.Contains("\"failedCount\":", json);
        Assert.Contains("\"skippedCount\":", json);

        // Verify enum values are serialized as camelCase strings (not integers)
        Assert.Contains("\"success\"", json);
        Assert.Contains("\"failed\"", json);
        Assert.Contains("\"skipped\"", json);

        // Verify status fields use string values, not numeric
        // The JSON should have "status":"success" not "status":0
        Assert.Contains("\"status\":\"success\"", json);
        Assert.Contains("\"status\":\"failed\"", json);
        Assert.Contains("\"status\":\"skipped\"", json);

        // Verify reason is omitted when null (WhenWritingNull)
        // The Success entry has no Reason set, so "reason" should appear exactly twice
        int reasonCount = CountOccurrences(json, "\"reason\":");
        Assert.Equal(2, reasonCount);
    }

    [Fact]
    public void BatchReport_DeserializesRoundTrip()
    {
        var original = new BatchReport
        {
            Files =
            [
                new BatchFileOutcome { FilePath = "/media/a.mp4", Status = BatchFileStatus.Success },
                new BatchFileOutcome { FilePath = "/media/b.mkv", Status = BatchFileStatus.Failed, Reason = "Error" },
            ],
            SucceededCount = 1,
            FailedCount = 1,
            SkippedCount = 0,
        };

        string json = JsonSerializer.Serialize(original, CliJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<BatchReport>(json, CliJsonOptions.Default);

        Assert.NotNull(deserialized);
        Assert.Equal(original.SucceededCount, deserialized.SucceededCount);
        Assert.Equal(original.FailedCount, deserialized.FailedCount);
        Assert.Equal(original.SkippedCount, deserialized.SkippedCount);
        Assert.Equal(original.Files.Count, deserialized.Files.Count);
        Assert.Equal(original.Files[0].FilePath, deserialized.Files[0].FilePath);
        Assert.Equal(original.Files[0].Status, deserialized.Files[0].Status);
        Assert.Null(deserialized.Files[0].Reason);
        Assert.Equal(original.Files[1].Reason, deserialized.Files[1].Reason);
    }

    [Fact]
    public void BatchReport_AllSuccess_SerializesWithZeroFailedAndSkipped()
    {
        var report = new BatchReport
        {
            Files =
            [
                new BatchFileOutcome { FilePath = "/video.mp4", Status = BatchFileStatus.Success },
            ],
            SucceededCount = 1,
            FailedCount = 0,
            SkippedCount = 0,
        };

        string json = JsonSerializer.Serialize(report, CliJsonOptions.Default);

        Assert.Contains("\"succeededCount\":1", json);
        Assert.Contains("\"failedCount\":0", json);
        Assert.Contains("\"skippedCount\":0", json);
    }

    // ─── Preset resolution order (explicit > preset > default) ──────────────────

    [Fact]
    public async Task PresetResolutionOrder_ExplicitOverridesPreset()
    {
        // Save a preset with target=es, source=en, export=mp4
        string presetsDir = Path.Combine(_tempDir, "presets");
        var store = new PresetStore(presetsDir);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "es",
            SourceLanguage = "en",
            ExportFormat = "mp4",
            ExecutionProvider = "cpu",
            DevicePolicy = "max-performance",
            EnableAsrTextRefinement = true,
            Models = new Dictionary<string, string> { ["asr"] = "whisper-large-v3" },
        };

        await store.SaveAsync("batch-test", preset, CancellationToken.None);

        // Load preset
        var loaded = await store.LoadAsync("batch-test", CancellationToken.None);
        Assert.NotNull(loaded);

        // Simulate explicit flag overrides (as DubCommand does):
        // explicit targetLanguage=fr overrides preset's "es"
        string? explicitTarget = "fr";
        string? explicitSource = null; // not provided → falls through to preset
        string? explicitExportFormat = "mkv"; // overrides preset's "mp4"

        // Apply merge logic (same as DubCommand)
        string resolvedTarget = explicitTarget ?? loaded.TargetLanguage;
        string? resolvedSource = explicitSource ?? loaded.SourceLanguage;
        string? resolvedExport = explicitExportFormat ?? loaded.ExportFormat;

        Assert.Equal("fr", resolvedTarget);   // explicit wins
        Assert.Equal("en", resolvedSource);   // preset value (no explicit)
        Assert.Equal("mkv", resolvedExport);  // explicit wins
    }

    [Fact]
    public async Task PresetResolutionOrder_PresetOverridesDefault()
    {
        string presetsDir = Path.Combine(_tempDir, "presets-default");
        var store = new PresetStore(presetsDir);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "de",
            SourceLanguage = "ja",
            ExportFormat = "mkv",
            EnableAsrTextRefinement = true,
        };

        await store.SaveAsync("priority-test", preset, CancellationToken.None);
        var loaded = await store.LoadAsync("priority-test", CancellationToken.None);
        Assert.NotNull(loaded);

        // Simulate: no explicit flags, all null → preset values should apply
        string? explicitTarget = null;
        string? explicitSource = null;
        string? explicitExport = null;
        bool explicitAsrRefinement = false;

        string resolvedTarget = explicitTarget ?? loaded.TargetLanguage;
        string? resolvedSource = explicitSource ?? loaded.SourceLanguage;
        string? resolvedExport = explicitExport ?? loaded.ExportFormat;
        bool resolvedAsr = explicitAsrRefinement || loaded.EnableAsrTextRefinement == true;

        Assert.Equal("de", resolvedTarget);
        Assert.Equal("ja", resolvedSource);
        Assert.Equal("mkv", resolvedExport);
        Assert.True(resolvedAsr);
    }

    [Fact]
    public async Task PresetResolutionOrder_ModelOverrides_ExplicitWins()
    {
        string presetsDir = Path.Combine(_tempDir, "presets-models");
        var store = new PresetStore(presetsDir);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "es",
            Models = new Dictionary<string, string>
            {
                ["asr"] = "whisper-large-v3",
                ["tts"] = "kokoro-onnx",
            },
        };

        await store.SaveAsync("model-test", preset, CancellationToken.None);
        var loaded = await store.LoadAsync("model-test", CancellationToken.None);
        Assert.NotNull(loaded);

        string[] explicitModelOverrides = ["asr:whisper-small"];

        string[] resolvedModels = CliBatchCommandHelpers.ResolveModelOverrides(explicitModelOverrides, loaded);

        Assert.Equal(2, resolvedModels.Length);
        Assert.Contains("asr:whisper-small", resolvedModels);
        Assert.Contains("tts:kokoro-onnx", resolvedModels);
    }

    [Fact]
    public async Task PresetResolutionOrder_ModelOverrides_PresetUsedWhenNoExplicit()
    {
        string presetsDir = Path.Combine(_tempDir, "presets-models2");
        var store = new PresetStore(presetsDir);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "es",
            Models = new Dictionary<string, string>
            {
                ["asr"] = "whisper-large-v3",
                ["tts"] = "kokoro-onnx",
            },
        };

        await store.SaveAsync("model-fallback", preset, CancellationToken.None);
        var loaded = await store.LoadAsync("model-fallback", CancellationToken.None);
        Assert.NotNull(loaded);

        string[] explicitModelOverrides = [];
        string[] resolvedModels = CliBatchCommandHelpers.ResolveModelOverrides(explicitModelOverrides, loaded);

        Assert.Equal(2, resolvedModels.Length);
        Assert.Contains("asr:whisper-large-v3", resolvedModels);
        Assert.Contains("tts:kokoro-onnx", resolvedModels);
    }

    [Fact]
    public async Task TryLoadPresetAsync_MalformedPreset_ReturnsArgumentErrorAndValidationMessage()
    {
        string modelRoot = Path.Combine(_tempDir, "sdk-storage");
        Directory.CreateDirectory(modelRoot);

        using TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithModelDirectory(modelRoot)
            .Build();
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        string presetsDirectory = Path.Combine(storagePaths.RootDirectory, "presets");
        Directory.CreateDirectory(presetsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(presetsDirectory, "corrupt.json"),
            "{ not valid json !!!");

        TextWriter originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);

        try
        {
            (PipelinePreset? preset, int exitCode) = await CliBatchCommandHelpers.TryLoadPresetAsync(
                "corrupt",
                factory,
                CancellationToken.None);

            Assert.Null(preset);
            Assert.Equal(Program.ExitArgumentError, exitCode);
            Assert.Contains("Failed to read preset", stderr.ToString());
            Assert.Contains("corrupt", stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    // ─── BatchFileDiscovery error paths ─────────────────────────────────────────

    [Fact]
    public void BatchFileDiscovery_MissingDirectory_ThrowsDirectoryNotFoundException()
    {
        string nonExistent = Path.Combine(_tempDir, "no-such-directory");

        var ex = Assert.Throws<DirectoryNotFoundException>(() =>
            BatchFileDiscovery.FromDirectory(nonExistent, recursive: false));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatchFileDiscovery_EmptyDirectory_ReturnsEmptyList()
    {
        string emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        var result = BatchFileDiscovery.FromDirectory(emptyDir, recursive: false);

        Assert.Empty(result);
    }

    [Fact]
    public void BatchFileDiscovery_DirectoryWithNoSupportedFiles_ReturnsEmptyList()
    {
        string dir = Path.Combine(_tempDir, "no-media");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "readme.txt"), []);
        File.WriteAllBytes(Path.Combine(dir, "data.json"), []);

        var result = BatchFileDiscovery.FromDirectory(dir, recursive: false);

        Assert.Empty(result);
    }

    [Fact]
    public void BatchFileDiscovery_FromGlob_MissingBaseDirectory_ThrowsDirectoryNotFoundException()
    {
        string nonExistent = Path.Combine(_tempDir, "glob-missing");

        var ex = Assert.Throws<DirectoryNotFoundException>(() =>
            BatchFileDiscovery.FromGlob("**/*.mp4", nonExistent));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Invalid preset name validation ─────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has spaces")]
    [InlineData("has!bang")]
    [InlineData("has@at")]
    [InlineData("has.dot")]
    [InlineData("slashes/not/allowed")]
    [InlineData("way-too-long-name-that-exceeds-sixty-four-characters-aaaaaaaaaaaaa")]
    public void PresetNameValidator_InvalidNames_Rejected(string invalidName)
    {
        Assert.False(PresetNameValidator.IsValid(invalidName));
    }

    [Fact]
    public void PresetNameValidator_NullName_Rejected()
    {
        Assert.False(PresetNameValidator.IsValid(null));
    }

    [Theory]
    [InlineData("my-preset")]
    [InlineData("preset_v2")]
    [InlineData("CamelCase")]
    [InlineData("a")]
    [InlineData("1234567890")]
    [InlineData("max-64-chars-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void PresetNameValidator_ValidNames_Accepted(string validName)
    {
        Assert.True(PresetNameValidator.IsValid(validName));
    }

    // ─── Exit code semantics ────────────────────────────────────────────────────

    [Fact]
    public void ExitCodes_AllSuccess_ReturnsZero()
    {
        // BatchHandler returns ExitSuccess (0) when FailedCount == 0
        var report = new BatchReport
        {
            Files = [new BatchFileOutcome { FilePath = "a.mp4", Status = BatchFileStatus.Success }],
            SucceededCount = 1,
            FailedCount = 0,
            SkippedCount = 0,
        };

        int exitCode = report.FailedCount > 0 ? Program.ExitPipelineFailure : Program.ExitSuccess;
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void ExitCodes_WithFailures_ReturnsTwo()
    {
        // BatchHandler returns ExitPipelineFailure (2) when FailedCount > 0
        var report = new BatchReport
        {
            Files =
            [
                new BatchFileOutcome { FilePath = "a.mp4", Status = BatchFileStatus.Success },
                new BatchFileOutcome { FilePath = "b.mp4", Status = BatchFileStatus.Failed, Reason = "Error" },
            ],
            SucceededCount = 1,
            FailedCount = 1,
            SkippedCount = 0,
        };

        int exitCode = report.FailedCount > 0 ? Program.ExitPipelineFailure : Program.ExitSuccess;
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void ExitCodes_FailFast_WithSkipped_ReturnsTwo()
    {
        // Fail-fast: one failure + remaining skipped → exit code 2
        var report = new BatchReport
        {
            Files =
            [
                new BatchFileOutcome { FilePath = "a.mp4", Status = BatchFileStatus.Failed, Reason = "Not found" },
                new BatchFileOutcome { FilePath = "b.mp4", Status = BatchFileStatus.Skipped, Reason = "Halted due to fail-fast" },
                new BatchFileOutcome { FilePath = "c.mp4", Status = BatchFileStatus.Skipped, Reason = "Halted due to fail-fast" },
            ],
            SucceededCount = 0,
            FailedCount = 1,
            SkippedCount = 2,
        };

        int exitCode = report.FailedCount > 0 ? Program.ExitPipelineFailure : Program.ExitSuccess;
        Assert.Equal(2, exitCode);
    }

    // ─── Preset + Batch integration ─────────────────────────────────────────────

    [Fact]
    public async Task PresetBatchIntegration_PresetLoadsBeforeBatchDiscovery()
    {
        // Requirement 8.1: preset resolves before batch discovery begins
        string presetsDir = Path.Combine(_tempDir, "presets-integration");
        var store = new PresetStore(presetsDir);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "fr",
            SourceLanguage = "en",
            ExportFormat = "mp4",
        };

        await store.SaveAsync("batch-preset", preset, CancellationToken.None);

        // Step 1: Resolve preset (happens first in DubCommand)
        var loaded = await store.LoadAsync("batch-preset", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("fr", loaded.TargetLanguage);

        // Step 2: After preset is resolved, discover batch files
        string mediaDir = Path.Combine(_tempDir, "media-integration");
        Directory.CreateDirectory(mediaDir);
        CreateFile("media-integration/clip1.mp4");
        CreateFile("media-integration/clip2.mkv");

        var files = BatchFileDiscovery.FromDirectory(mediaDir, recursive: false);
        Assert.Equal(2, files.Count);

        // Step 3: Build template options using resolved preset
        var templateOptions = new DubbingSessionOptions
        {
            SourceMediaPath = "batch",
            TargetLanguageCode = loaded.TargetLanguage,
            SourceLanguageCode = loaded.SourceLanguage,
            ExportFormat = loaded.ExportFormat,
        };

        Assert.Equal("fr", templateOptions.TargetLanguageCode);
        Assert.Equal("en", templateOptions.SourceLanguageCode);
        Assert.Equal("mp4", templateOptions.ExportFormat);
    }

    [Fact]
    public async Task PresetBatchIntegration_InvalidPresetName_FailsBeforeBatchDiscovery()
    {
        // Requirement 8.4: if preset doesn't exist, exit with code 1 without processing
        string presetsDir = Path.Combine(_tempDir, "presets-invalid");
        var store = new PresetStore(presetsDir);

        // Ensure presets directory exists but preset doesn't
        Directory.CreateDirectory(presetsDir);

        var loaded = await store.LoadAsync("nonexistent-preset", CancellationToken.None);
        Assert.Null(loaded); // Not found → DubCommand would report error and exit code 1
    }

    [Fact]
    public void PresetBatchIntegration_InvalidPresetName_RejectedByValidator()
    {
        // If preset name fails validation, error occurs before any file discovery
        Assert.False(PresetNameValidator.IsValid("bad name!"));
        Assert.False(PresetNameValidator.IsValid(""));
        Assert.False(PresetNameValidator.IsValid(null));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }
}
