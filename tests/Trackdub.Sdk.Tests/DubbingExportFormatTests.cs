using System.Reflection;
using Trackdub.Contracts;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Sdk.Tests;

public sealed class DubbingExportFormatTests
{
    [Fact]
    public void ResolveExportContainer_DefaultsToMp4()
    {
        Assert.Equal(
            ExportOutputContainer.Mp4,
            TrackdubDubbingEngine.ResolveExportContainer(null));
    }

    [Fact]
    public void ResolveExportContainer_MapsMkv()
    {
        Assert.Equal(
            ExportOutputContainer.Mkv,
            TrackdubDubbingEngine.ResolveExportContainer("mkv"));
    }

    [Fact]
    public void ResolveExportOutputPath_UsesContainerExtension()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), $"trackdub-{Guid.NewGuid():N}");

        string outputPath = TrackdubDubbingEngine.ResolveExportOutputPath(projectRoot, ExportOutputContainer.Mkv);

        Assert.Equal(Path.Combine(projectRoot, "exports", "dubbed.mkv"), outputPath);
    }

    [Fact]
    public void ShouldSkipModelPreFlight_skips_translation_when_deepl_cloud_is_selected()
    {
        bool skip = InvokeShouldSkipModelPreFlight(
            StageNames.Translation,
            new Dictionary<string, string>
            {
                [StageNames.Translation] = TranslationModelOverrideSettings.DeepLModelAlias
            });

        Assert.True(skip);
    }

    private static bool InvokeShouldSkipModelPreFlight(
        string stageName,
        IReadOnlyDictionary<string, string>? modelPreferences)
    {
        MethodInfo method = typeof(TrackdubDubbingEngine).GetMethod(
            "ShouldSkipModelPreFlight",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(TrackdubDubbingEngine), "ShouldSkipModelPreFlight");
        return (bool)method.Invoke(null, [stageName, modelPreferences])!;
    }
}
