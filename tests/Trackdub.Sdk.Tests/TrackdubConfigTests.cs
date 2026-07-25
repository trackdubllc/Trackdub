using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Trackdub.Cli;
using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

[Collection(nameof(CliStdoutCaptureCollection))]
public sealed class TrackdubConfigTests
{
    [Fact]
    public async Task ConfigPathsCommand_EmitsStorageAndManifestPaths()
    {
        StringWriter stdout = new();
        TextWriter originalOut = Console.Out;
        Console.SetOut(stdout);

        try
        {
            RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
            ParseResult parseResult = rootCommand.Parse(["config", "paths"]);
            int exitCode = await parseResult.InvokeAsync();

            Assert.Equal(Program.ExitSuccess, exitCode);

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;

            Assert.True(root.TryGetProperty("userDataRoot", out JsonElement userDataRoot));
            Assert.False(string.IsNullOrWhiteSpace(userDataRoot.GetString()));

            Assert.True(root.TryGetProperty("modelCacheDirectory", out JsonElement modelCacheDirectory));
            Assert.False(string.IsNullOrWhiteSpace(modelCacheDirectory.GetString()));

            Assert.True(root.TryGetProperty("logFilePath", out JsonElement logFilePath));
            Assert.EndsWith("trackdub.log", logFilePath.GetString(), StringComparison.OrdinalIgnoreCase);

            Assert.True(root.TryGetProperty("settingsPath", out JsonElement settingsPath));
            Assert.EndsWith("settings.json", settingsPath.GetString(), StringComparison.OrdinalIgnoreCase);

            if (root.TryGetProperty("bundledManifestPath", out JsonElement manifestPath)
                && manifestPath.ValueKind == JsonValueKind.String)
            {
                Assert.EndsWith(
                    "bundled-models.manifest.json",
                    manifestPath.GetString(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task ConfigShowCommand_IncludesPathsAndSettingsSummary()
    {
        StringWriter stdout = new();
        TextWriter originalOut = Console.Out;
        Console.SetOut(stdout);

        try
        {
            RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
            ParseResult parseResult = rootCommand.Parse(["config", "show"]);
            int exitCode = await parseResult.InvokeAsync();

            Assert.Equal(Program.ExitSuccess, exitCode);

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;

            Assert.True(root.TryGetProperty("paths", out JsonElement paths));
            Assert.True(paths.TryGetProperty("settingsPath", out _));
            Assert.True(root.TryGetProperty("settingsFileExists", out JsonElement settingsFileExists));
            Assert.True(
                settingsFileExists.ValueKind is JsonValueKind.True or JsonValueKind.False);

            Assert.True(root.TryGetProperty("recentProjects", out JsonElement recentProjects));
            Assert.Equal(JsonValueKind.Array, recentProjects.ValueKind);

            Assert.True(root.TryGetProperty("modelTierPreference", out JsonElement modelTierPreference));
            Assert.False(string.IsNullOrWhiteSpace(modelTierPreference.GetString()));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task TrackdubConfig_CaptureShow_ReadsRecentProjectsFromSettingsFile()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        string projectPath = Path.Combine(tempRoot, "demo.trackdub");
        Directory.CreateDirectory(projectPath);

        string settingsPath = Path.Combine(tempRoot, "settings.json");
        string settingsJson = $$"""
            {
              "defaultSourceLanguage": "en",
              "defaultTargetLanguage": "es",
              "modelTierPreference": "balanced",
              "windowLayout": { "width": 1280, "height": 720, "isMaximized": false },
              "recentProjects": [
                {
                  "projectName": "Demo",
                  "projectPath": "{{projectPath.Replace("\\", "\\\\")}}",
                  "lastOpenedAtUtc": "2026-06-06T12:00:00Z"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(settingsPath, settingsJson);

        try
        {
            var storagePaths = new TestAppStoragePaths(tempRoot);
            TrackdubSessionFactory factory = new TrackdubBuilder()
                .ConfigureServices(services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<IAppStoragePaths>(storagePaths));
                })
                .Build();

            using (factory)
            {
                TrackdubConfigShowSnapshot snapshot = await TrackdubConfig.CaptureShowAsync(
                    factory,
                    CancellationToken.None);

                Assert.True(snapshot.SettingsFileExists);
                Assert.Null(snapshot.SettingsReadError);
                Assert.Single(snapshot.RecentProjects);
                Assert.Equal("Demo", snapshot.RecentProjects[0].ProjectName);
                Assert.Equal(Path.GetFullPath(projectPath), snapshot.RecentProjects[0].ProjectPath);
                Assert.Equal("en", snapshot.DefaultSourceLanguage);
                Assert.Equal("es", snapshot.DefaultTargetLanguage);
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class TestAppStoragePaths(string userDataRoot) : IAppStoragePaths
    {
        public string RootDirectory { get; } = userDataRoot;

        public string UserDataRoot { get; } = userDataRoot;

        public string UserCacheRoot { get; } = userDataRoot;

        public string? SharedAssetRoot => null;

        public bool IsPortable => false;

        public string ModelCacheDirectory { get; } = Path.Combine(userDataRoot, "model-cache");

        public string ModelCacheIndexPath { get; } = Path.Combine(userDataRoot, "model-cache", "model-cache-records.json");

        public string LogFilePath { get; } = Path.Combine(userDataRoot, "trackdub.log");

        public string SettingsPath { get; } = Path.Combine(userDataRoot, "settings.json");

        public string LayoutPath { get; } = Path.Combine(userDataRoot, "avalonia-layout.json");

        public string ToolCacheDirectory { get; } = Path.Combine(userDataRoot, "tools");

        public string FfmpegToolCacheDirectory { get; } = Path.Combine(userDataRoot, "tools", "ffmpeg");

        public string EngineCacheDirectory { get; } = Path.Combine(userDataRoot, "EngineCache");

        public string ComponentCacheDirectory { get; } = Path.Combine(userDataRoot, "components");
    }
}
