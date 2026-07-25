using Trackdub.Domain.Translation;

namespace Trackdub.Domain.Tests;

public sealed class GlossaryEntryTests
{
    [Fact]
    public void Create_Normalizes_language_codes_and_terms()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.Parse("2026-05-07T12:00:00+00:00");

        GlossaryEntry entry = GlossaryEntry.Create(
            projectId,
            " EN ",
            " ES ",
            "  Warp Core ",
            " Nucleo Warp ",
            isCaseSensitive: true,
            now);

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(projectId, entry.ProjectId);
        Assert.Equal("en", entry.SourceLanguage);
        Assert.Equal("es", entry.TargetLanguage);
        Assert.Equal("Warp Core", entry.SourceTerm);
        Assert.Equal("Nucleo Warp", entry.TargetTerm);
        Assert.True(entry.IsCaseSensitive);
        Assert.Equal(now, entry.CreatedAtUtc);
        Assert.Equal(now, entry.UpdatedAtUtc);
    }

    [Fact]
    public void Create_Rejects_empty_project_id()
    {
        Assert.Throws<ArgumentException>(() => GlossaryEntry.Create(
            Guid.Empty,
            "en",
            "es",
            "server",
            "servidor",
            isCaseSensitive: false,
            DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("", "es", "server", "servidor")]
    [InlineData("en", "", "server", "servidor")]
    [InlineData("en", "es", "", "servidor")]
    [InlineData("en", "es", "server", "")]
    public void Create_Rejects_empty_language_codes_and_terms(
        string sourceLanguage,
        string targetLanguage,
        string sourceTerm,
        string targetTerm)
    {
        Assert.Throws<ArgumentException>(() => GlossaryEntry.Create(
            Guid.NewGuid(),
            sourceLanguage,
            targetLanguage,
            sourceTerm,
            targetTerm,
            isCaseSensitive: false,
            DateTimeOffset.UtcNow));
    }
}
