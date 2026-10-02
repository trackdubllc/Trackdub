using Trackdub.TestDoubles;
using Trackdub.Contracts;
using Trackdub.Domain.Tts;
using Trackdub.Application.Transcripts;

namespace Trackdub.Application.Tests;

public sealed class Qwen3TtsPresetDefaultsTests
{
    [Theory]
    [InlineData("zh", "qwen3:vivian")]
    [InlineData("zh-Hans", "qwen3:vivian")]
    [InlineData("ja", "qwen3:ono_anna")]
    [InlineData("ko", "qwen3:sohee")]
    [InlineData("en", "qwen3:ryan")]
    [InlineData("fr", "qwen3:ryan")]
    [InlineData(null, "qwen3:ryan")]
    public void ResolveDefaultPresetVoiceId_PrefersANativeSpeaker(string? targetLanguage, string expectedVoiceId) =>
        Assert.Equal(expectedVoiceId, Qwen3TtsDefaults.ResolveDefaultPresetVoiceId(targetLanguage));

    [Theory]
    [InlineData("qwen3:vivian", true)]
    [InlineData("QWEN3:Uncle_Fu", true)]
    [InlineData(" qwen3:ryan ", true)]
    [InlineData("qwen3:", false)]
    [InlineData("qwen3:unknown", false)]
    [InlineData("af_heart", false)]
    [InlineData(null, false)]
    public void IsPresetVoiceId_RecognizesQwen3PresetIds(string? voiceId, bool expected) =>
        Assert.Equal(expected, Qwen3TtsDefaults.IsPresetVoiceId(voiceId));

    [Theory]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("en_US", true)]
    [InlineData("nl", false)]
    [InlineData("ar", false)]
    [InlineData(null, false)]
    public void SupportsLanguage_NormalizesLocalesAndRejectsUnsupportedLanguages(string? language, bool expected) =>
        Assert.Equal(expected, Qwen3TtsDefaults.SupportsLanguage(language));

    [Fact]
    public void BuildWarnings_DoesNotFlagQwen3PresetAssignmentsAsLanguageMismatches()
    {
        var speakerId = Guid.NewGuid();
        VoiceAssignment assignment = VoiceAssignment.Create(Guid.NewGuid(), speakerId, "qwen3-tts-0.6b-customvoice", "qwen3:vivian");
        var service = new VoiceAssignmentService(new FakeVoiceAssignmentRepository(), new FakeTtsTakeRepository(), new FakeVoiceCatalog());

        IReadOnlyList<VoiceAssignmentWarning> warnings = service.BuildWarnings(
            [assignment],
            [new VoiceCatalogEntry("qwen3:vivian", "mul", "female", "Vivian (Chinese)")],
            "zh");

        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildWarnings_StillFlagsQwen3PresetAssignmentsForUnsupportedTargets()
    {
        VoiceAssignment assignment = VoiceAssignment.Create(Guid.NewGuid(), Guid.NewGuid(), "qwen3-tts-0.6b-customvoice", "qwen3:ryan");
        var service = new VoiceAssignmentService(new FakeVoiceAssignmentRepository(), new FakeTtsTakeRepository(), new FakeVoiceCatalog());

        IReadOnlyList<VoiceAssignmentWarning> warnings = service.BuildWarnings(
            [assignment],
            [new VoiceCatalogEntry("qwen3:ryan", "mul", "male", "Ryan (English)")],
            "nl");

        Assert.Single(warnings);
    }
}
