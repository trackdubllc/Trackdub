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
            string mediaPath = Path.Combine(tempDirectory, "clip.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "clip");

            Assert.Equal("clip", candidate.ProjectName);
            Assert.Equal(Path.Combine(tempDirectory, "clip.trackdub"), candidate.ProjectRootPath);
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
            Directory.CreateDirectory(Path.Combine(tempDirectory, "clip.trackdub"));
            Directory.CreateDirectory(Path.Combine(tempDirectory, "clip #2.trackdub"));
            string mediaPath = Path.Combine(tempDirectory, "clip.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "clip");

            Assert.Equal("clip #3", candidate.ProjectName);
            Assert.Equal(Path.Combine(tempDirectory, "clip #3.trackdub"), candidate.ProjectRootPath);
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
            File.WriteAllText(Path.Combine(tempDirectory, "clip.trackdub"), "not a project folder");
            string mediaPath = Path.Combine(tempDirectory, "clip.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "clip");

            Assert.Equal("clip #2", candidate.ProjectName);
            Assert.Equal(Path.Combine(tempDirectory, "clip #2.trackdub"), candidate.ProjectRootPath);
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
            Directory.CreateDirectory(Path.Combine(tempDirectory, "CON_.trackdub"));
            string mediaPath = Path.Combine(tempDirectory, "CON.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "CON");

            Assert.Equal("CON_ #2", candidate.ProjectName);
            Assert.Equal(Path.Combine(tempDirectory, "CON_ #2.trackdub"), candidate.ProjectRootPath);
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
            string mediaPath = Path.Combine(tempDirectory, "clip.mp4");

            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, projectName);

            Assert.Equal(expectedProjectName, candidate.ProjectName);
            Assert.Equal(Path.Combine(tempDirectory, $"{expectedProjectName}.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveProjectParentDirectory_uses_local_projects_folder_for_onedrive_media()
    {
        string mediaPath = CreateCloudSyncedMediaPath("Movies", "clip.mp4");
        string userDataRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Application.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            string parent = ProjectRootNameResolver.ResolveProjectParentDirectory(mediaPath, userDataRoot);

            Assert.Equal(Path.Combine(userDataRoot, "projects"), parent);
            Assert.True(Directory.Exists(parent));
        }
        finally
        {
            if (Directory.Exists(userDataRoot))
            {
                Directory.Delete(userDataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateAvailableProjectRoot_places_onedrive_media_project_under_local_projects_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string userDataRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Application.Tests", Guid.NewGuid().ToString("N"));
        string mediaPath = CreateCloudSyncedMediaPath("Movies", "clip.mp4");

        try
        {
            string projectParent = ProjectRootNameResolver.ResolveProjectParentDirectory(mediaPath, userDataRoot);
            ProjectRootNameCandidate candidate = ProjectRootNameResolver.CreateAvailableProjectRoot(
                mediaPath,
                "clip",
                projectParent);

            Assert.Equal("clip", candidate.ProjectName);
            Assert.Equal(Path.Combine(userDataRoot, "projects", "clip.trackdub"), candidate.ProjectRootPath);
        }
        finally
        {
            if (Directory.Exists(userDataRoot))
            {
                Directory.Delete(userDataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateAvailableProjectRoot_rejects_explicit_cloud_synced_project_parent()
    {
        string mediaPath = CreateCloudSyncedMediaPath("Movies", "clip.mp4");
        string projectParent = Path.GetDirectoryName(mediaPath)!;

        IOException exception = Assert.Throws<IOException>(() =>
            ProjectRootNameResolver.CreateAvailableProjectRoot(mediaPath, "clip", projectParent));

        Assert.Contains("cloud-synced folder", exception.Message);
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Trackdub.Application.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateCloudSyncedMediaPath(string relativeFolder, string fileName)
    {
        string mediaDirectory = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.Application.Tests",
            Guid.NewGuid().ToString("N"),
            "OneDrive",
            "Videos",
            relativeFolder);
        Directory.CreateDirectory(mediaDirectory);
        return Path.Combine(mediaDirectory, fileName);
    }
}
