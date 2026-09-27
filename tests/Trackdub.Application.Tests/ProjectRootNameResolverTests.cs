using Trackdub.Application.Projects;

namespace Trackdub.Application.Tests;

public sealed class ProjectRootNameResolverTests
{
    [Fact]
    public void CreateAvailableProjectRoot_uses_media_name_when_project_folder_is_available()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string mediaPath = Path.Join(tempDirectory, "clip.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "clip");

            Assert.Equal("clip", candidate.ProjectName);
            Assert.Equal(Path.Join(tempDirectory, "clip.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreateAvailableProjectRoot_appends_next_copy_number_when_project_folder_exists()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Join(tempDirectory, "clip.trackdub"));
            Directory.CreateDirectory(Path.Join(tempDirectory, "clip #2.trackdub"));
            string mediaPath = Path.Join(tempDirectory, "clip.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "clip");

            Assert.Equal("clip #3", candidate.ProjectName);
            Assert.Equal(Path.Join(tempDirectory, "clip #3.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreateAvailableProjectRoot_treats_file_conflict_like_existing_project()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Join(tempDirectory, "clip.trackdub"), "not a project folder");
            string mediaPath = Path.Join(tempDirectory, "clip.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "clip");

            Assert.Equal("clip #2", candidate.ProjectName);
            Assert.Equal(Path.Join(tempDirectory, "clip #2.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreateAvailableProjectRoot_sanitizes_reserved_project_name_before_numbering()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Join(tempDirectory, "CON_.trackdub"));
            string mediaPath = Path.Join(tempDirectory, "CON.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "CON");

            Assert.Equal("CON_ #2", candidate.ProjectName);
            Assert.Equal(Path.Join(tempDirectory, "CON_ #2.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("CON.txt", "CON_.txt")]
    [InlineData("aux.project", "aux_.project")]
    [InlineData("COM1.notes", "COM1_.notes")]
    [InlineData("LPT9.review", "LPT9_.review")]
    public void CreateAvailableProjectRoot_sanitizes_reserved_project_name_before_extension_suffix(
        string projectName,
        string expectedProjectName)
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string mediaPath = Path.Join(tempDirectory, "clip.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, projectName);

            Assert.Equal(expectedProjectName, candidate.ProjectName);
            Assert.Equal(Path.Join(tempDirectory, $"{expectedProjectName}.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveProjectParentDirectory_returns_media_parent_for_onedrive_media()
    {
        string mediaPath = CreateCloudSyncedMediaPath("Movies", "clip.mp4");
        string mediaDirectory = Path.GetDirectoryName(mediaPath)!;

        try
        {
            string parent = ProjectRootNameResolver.ResolveProjectParentDirectory(mediaPath);

            Assert.Equal(mediaDirectory, parent);
        }
        finally
        {
            string oneDriveRoot = Path.GetDirectoryName(Path.GetDirectoryName(mediaDirectory))!;
            if (Directory.Exists(oneDriveRoot))
            {
                Directory.Delete(oneDriveRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateAvailableProjectRoot_places_onedrive_media_project_beside_media()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Application.Tests", Guid.NewGuid().ToString("N"));
        string mediaDirectory = Path.Join(tempRoot, "OneDrive", "Videos", "Movies");
        Directory.CreateDirectory(mediaDirectory);
        string mediaPath = Path.Join(mediaDirectory, "clip.mp4");

        try
        {
            string projectParent = ProjectRootNameResolver.ResolveProjectParentDirectory(mediaPath);
            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(
                mediaPath,
                "clip",
                projectParent);

            Assert.Equal("clip", candidate.ProjectName);
            Assert.Equal(Path.Join(mediaDirectory, "clip.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateAvailableProjectRoot_allows_explicit_cloud_synced_project_parent()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Application.Tests", Guid.NewGuid().ToString("N"));
        string mediaDirectory = Path.Join(tempRoot, "OneDrive", "Videos", "Movies");
        Directory.CreateDirectory(mediaDirectory);
        string mediaPath = Path.Join(mediaDirectory, "clip.mp4");

        try
        {
            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(
                mediaPath,
                "clip",
                mediaDirectory);

            Assert.Equal(Path.Join(mediaDirectory, "clip.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Join(Path.GetTempPath(), "Trackdub.Application.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateCloudSyncedMediaPath(string relativeFolder, string fileName)
    {
        string mediaDirectory = Path.Join(
            Path.GetTempPath(),
            "Trackdub.Application.Tests",
            Guid.NewGuid().ToString("N"),
            "OneDrive",
            "Videos",
            relativeFolder);
        Directory.CreateDirectory(mediaDirectory);
        return Path.Join(mediaDirectory, fileName);
    }
}
