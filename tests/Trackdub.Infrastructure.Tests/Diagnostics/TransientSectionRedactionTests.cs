using System.IO.Compression;
using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Diagnostics;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Pipeline;
using Trackdub.Infrastructure.Diagnostics;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests.Diagnostics;

/// <summary>
/// Per <c>docs/internal/pipeline-readiness-spec.md</c> §9.3 (bundle redaction)
/// + §11.6 (test surface). Inherits the existing
/// <see cref="UserProfilePathRedactor"/> for the new transient-fault summary
/// section so a packed diagnostics bundle does not leak absolute
/// user-profile paths in <see cref="PipelineTransientFault.Detail"/> or
/// <see cref="PipelineTransientFault.Context"/>. See
/// <c>src/Trackdub.Infrastructure/Diagnostics/DiagnosticsBundleExporter.cs</c>
/// line <c>if (request.Transient is ...)</c>: the serialised transient
/// JSON is wrapped in <c>RedactPaths</c> at write time.
/// </summary>
public sealed class TransientSectionRedactionTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    [Fact]
    public async Task RedactPaths_passes_through_transient_section_string()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        Directory.CreateDirectory(storagePaths.RootDirectory);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string leakyDetail = $@"ASR cache miss at {userProfile}\.cache\models\whisper\weights.onnx";

        var busFault = new PipelineTransientFault(
            projectId: Guid.NewGuid(),
            stageName: "Asr",
            kind: TransientFailureKind.DirectoryLock,
            detail: leakyDetail,
            happenedAt: DateTimeOffset.UtcNow,
            attemptNumber: 1,
            context: new Dictionary<string, string>
            {
                ["path"] = $@"{userProfile}\.cache\models\whisper\weights.onnx",
            });

        TransientFaultSummary transient = TransientFaultSummary.From(new[] { busFault });

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);

        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(
            outputPath,
            Transient: transient));

        using ZipArchive archive = ZipFile.OpenRead(outputPath);
        Assert.NotNull(archive.GetEntry("transient-fault-summary.json"));
        string transientJson = await ReadEntryAsync(archive, "transient-fault-summary.json");

        // Hard guarantee: the raw current user name never appears in the
        // packed transient section. Catches the most common leak — the
        // test author's identity into a shared diagnostics bundle.
        // Guard mirrors the soft-assertion below: container runners may
        // return an empty user name, and Assert.DoesNotContain("", …)
        // always throws because every string "contains" the empty string.
        if (!string.IsNullOrEmpty(Environment.UserName))
        {
            Assert.DoesNotContain(
                Environment.UserName,
                transientJson,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(
            leakyDetail,
            transientJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            userProfile,
            transientJson,
            StringComparison.OrdinalIgnoreCase);

        // Soft redaction proof: when the UserProfilePathRedactor regex
        // fires it produces the <USER> token. We assert against the
        // JSON-encoded form (the diagnostic ZIP entry stores System.Text.Json
        // output where backslashes are escaped to two characters per
        // literal). Skip when the environment has no resolvable user name
        // (container runners may return empty) — the negative assertions
        // above already lock the §9.3 security invariant.
        if (!string.IsNullOrEmpty(Environment.UserName)
            && leakyDetail.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Contains(
                @"<USER>\\.cache\\models\\whisper\\weights.onnx",
                transientJson,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RedactPaths_does_not_mutate_unredacted_fields()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        Directory.CreateDirectory(storagePaths.RootDirectory);

        var busFault = new PipelineTransientFault(
            projectId: Guid.NewGuid(),
            stageName: "Translation",
            kind: TransientFailureKind.ModelDownloadTransient,
            detail: "Upstream translator returned 503; body had no user paths.",
            happenedAt: DateTimeOffset.UtcNow,
            attemptNumber: 2,
            context: new Dictionary<string, string>
            {
                ["endpoint"] = "https://api.example.test/translate",
                ["statusCode"] = "503",
            });

        TransientFaultSummary transient = TransientFaultSummary.From(new[] { busFault });

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);

        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(
            outputPath,
            Transient: transient));

        using ZipArchive archive = ZipFile.OpenRead(outputPath);
        string transientJson = await ReadEntryAsync(archive, "transient-fault-summary.json");

        using JsonDocument document = JsonDocument.Parse(transientJson);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("Total").GetInt32());
        Assert.Equal("Translation", root.GetProperty("MostRecent")[0].GetProperty("StageName").GetString());
        Assert.Equal(
            "Upstream translator returned 503; body had no user paths.",
            root.GetProperty("MostRecent")[0].GetProperty("Detail").GetString());
        Assert.Equal(
            "https://api.example.test/translate",
            root.GetProperty("MostRecent")[0].GetProperty("Context").GetProperty("endpoint").GetString());
        Assert.Equal(
            "503",
            root.GetProperty("MostRecent")[0].GetProperty("Context").GetProperty("statusCode").GetString());

        // Kind serialises as an integer ordinal under JsonConventions.DiagnosticsBundle
        // (no JsonStringEnumConverter registration); verify ordinal + absence of mutation,
        // not the string form.
        JsonElement kindElement = root.GetProperty("MostRecent")[0].GetProperty("Kind");
        Assert.Equal(JsonValueKind.Number, kindElement.ValueKind);
        Assert.Equal((int)TransientFailureKind.ModelDownloadTransient, kindElement.GetInt32());
        Assert.DoesNotContain("<USER>", transientJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedactPaths_handles_transient_section_with_no_user_profile_paths()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        Directory.CreateDirectory(storagePaths.RootDirectory);

        TransientFaultSummary emptyTransient = TransientFaultSummary.From(Array.Empty<PipelineTransientFault>());

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);

        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(
            outputPath,
            Transient: emptyTransient));

        using ZipArchive archive = ZipFile.OpenRead(outputPath);
        string transientJson = await ReadEntryAsync(archive, "transient-fault-summary.json");

        Assert.Equal(0, emptyTransient.Total);
        Assert.Empty(emptyTransient.MostRecent);
        using JsonDocument document = JsonDocument.Parse(transientJson);
        Assert.Equal(0, document.RootElement.GetProperty("Total").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("MostRecent").EnumerateArray());
        Assert.DoesNotContain("<USER>", transientJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportBundleAsync_omits_transient_section_when_request_Transient_is_null()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        Directory.CreateDirectory(storagePaths.RootDirectory);

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);

        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using ZipArchive archive = ZipFile.OpenRead(outputPath);
        Assert.Null(archive.GetEntry("transient-fault-summary.json"));
    }

    public void Dispose()
    {
        foreach (string directory in tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Zip entry '{path}' not found.");
        await using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        string text = await reader.ReadToEndAsync();
        return text.TrimEnd('\r', '\n');
    }

    private string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }
}
