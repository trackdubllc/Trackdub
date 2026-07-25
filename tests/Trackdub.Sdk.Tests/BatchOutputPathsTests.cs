using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class BatchOutputPathsTests
{
    [Fact]
    public void BuildUniqueProjectFolderName_SameStemDifferentParentDirs_ProducesDistinctNames()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string left = BatchOutputPaths.BuildUniqueProjectFolderName(Path.Combine(root, "a", "y", "clip.mp4"));
        string right = BatchOutputPaths.BuildUniqueProjectFolderName(Path.Combine(root, "b", "y", "clip.mp4"));

        Assert.NotEqual(left, right);
        Assert.Contains("_y_clip.mp4_", left, StringComparison.Ordinal);
        Assert.Contains("_y_clip.mp4_", right, StringComparison.Ordinal);
        Assert.EndsWith(".trackdub", left, StringComparison.Ordinal);
        Assert.EndsWith(".trackdub", right, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUniqueProjectFolderName_FlattenedPathSegments_DoNotCollide()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        string nested = BatchOutputPaths.BuildUniqueProjectFolderName(Path.Combine(root, "foo", "bar", "clip.mp4"));
        string flat = BatchOutputPaths.BuildUniqueProjectFolderName(Path.Combine(root, "foo_bar", "clip.mp4"));

        Assert.NotEqual(nested, flat);
    }

    [Fact]
    public void BuildProjectDirectory_CombinesOutputRootWithUniqueFolderName()
    {
        string media = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "batch", "a", "y", "clip.mp4"));
        string outputRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "out"));
        string outputDirectory = BatchOutputPaths.BuildProjectDirectory(media, outputRoot);

        Assert.StartsWith(outputRoot + Path.DirectorySeparatorChar, outputDirectory);
        Assert.Contains("_y_clip.mp4_", outputDirectory, StringComparison.Ordinal);
        Assert.EndsWith(".trackdub", outputDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUniqueProjectFolderName_DeepAbsolutePath_StaysWithinSegmentLimit()
    {
        string longSegment = new string('a', 120);
        string media = Path.GetFullPath(Path.Combine(Path.GetTempPath(), longSegment, longSegment, longSegment, "clip.mp4"));
        string folderName = BatchOutputPaths.BuildUniqueProjectFolderName(media);

        Assert.True(folderName.Length <= 240, $"Folder name length {folderName.Length} exceeds 240.");
        Assert.EndsWith(".trackdub", folderName, StringComparison.Ordinal);
        Assert.Contains("_clip.mp4_", folderName, StringComparison.Ordinal);
    }
}
