using Trackdub.Domain.Translation;

namespace Trackdub.Domain.Tests;

public sealed class GlossaryEntryScopeTests
{
    [Fact]
    public void Create_rejects_global_scope_project_id()
    {
        Assert.Throws<ArgumentException>(() =>
            GlossaryEntry.Create(
                GlossaryScopeIds.Global,
                "en",
                "es",
                "Server",
                "servidor",
                false,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CreateGlobal_uses_global_scope_id()
    {
        GlossaryEntry entry = GlossaryEntry.CreateGlobal("en", "es", "Server", "servidor", false, DateTimeOffset.UtcNow);

        Assert.Equal(GlossaryScopeIds.Global, entry.ProjectId);
        GlossaryEntry.ValidateGlobalScope(entry);
    }

    [Fact]
    public void ValidateProjectScope_rejects_global_scope_id()
    {
        Assert.Throws<ArgumentException>(() => GlossaryEntry.ValidateProjectScope(GlossaryScopeIds.Global));
    }
}
