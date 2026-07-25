using Trackdub.Contracts;
using Trackdub.TestDoubles;

namespace Trackdub.Infrastructure.Tests;

public sealed class FakeStudioSettingsServiceTests
{
    [Fact]
    public async Task TouchRecentProjectAsync_keeps_only_ten_entries_ordered_newest_first()
    {
        var service = new FakeStudioSettingsService();

        for (int index = 0; index < 11; index++)
        {
            await service.TouchRecentProjectAsync(
                $@"D:\Projects\Project{index}.trackdub",
                $"Project {index}",
                TestContext.Current.CancellationToken);
        }

        StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, loaded.RecentProjects.Count);
        Assert.DoesNotContain(loaded.RecentProjects, entry => entry.ProjectName == "Project 0");
        Assert.Equal("Project 10", loaded.RecentProjects[0].ProjectName);
    }
}
