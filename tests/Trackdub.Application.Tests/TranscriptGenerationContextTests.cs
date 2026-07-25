using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;

namespace Trackdub.Application.Tests;

public sealed class TranscriptGenerationContextTests
{
    [Fact]
    public void SpeechRegions_Default_IsEmptyArray()
    {
        TranscriptGenerationContext context = CreateMinimalContext();

        Assert.Empty(context.SpeechRegions);
        Assert.IsType<SpeechRegion[]>(GetSpeechRegionsBacking(context));
    }

    [Fact]
    public void SpeechRegions_WhenSetViaConstructor_DefensivelyCopiesToList()
    {
        var regions = new List<SpeechRegion>
        {
            new(0, 0.0, 1.0),
            new(1, 1.5, 3.0),
        };

        TranscriptGenerationContext context = CreateMinimalContext(speechRegions: regions);

        regions.Add(new SpeechRegion(2, 3.5, 5.0));

        Assert.Equal(2, context.SpeechRegions.Count);
    }

    [Fact]
    public void SpeechRegions_WhenSetViaWithExpression_DefensivelyCopiesToList()
    {
        var regions = new List<SpeechRegion>
        {
            new(0, 0.0, 1.0),
            new(1, 1.5, 3.0),
        };

        TranscriptGenerationContext context = CreateMinimalContext() with { SpeechRegions = regions };

        regions.Add(new SpeechRegion(2, 3.5, 5.0));

        Assert.Equal(2, context.SpeechRegions.Count);
    }

    [Fact]
    public void SpeechRegions_WhenSetViaWithExpression_BackingStoreIsArray()
    {
        var regions = new List<SpeechRegion>
        {
            new(0, 0.0, 1.0),
        };

        TranscriptGenerationContext context = CreateMinimalContext() with { SpeechRegions = regions };

        Assert.IsType<SpeechRegion[]>(GetSpeechRegionsBacking(context));
    }

    [Fact]
    public void SpeechRegions_WhenSetToArrayInWithExpression_SharedArrayCannotBeMutated()
    {
        SpeechRegion[] array = [new(0, 0.0, 1.0)];
        TranscriptGenerationContext context = CreateMinimalContext() with { SpeechRegions = array };

        array[0] = new SpeechRegion(0, 99.0, 100.0);

        Assert.Equal(0.0, context.SpeechRegions[0].StartSeconds);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance_OriginalRemainsUnchanged()
    {
        TranscriptGenerationContext original = CreateMinimalContext();
        TranscriptGenerationContext modified = original with
        {
            SpeechRegions = [new SpeechRegion(0, 0.0, 10.0)],
        };

        Assert.Empty(original.SpeechRegions);
        Assert.Single(modified.SpeechRegions);
    }

    [Fact]
    public void WithExpression_PreservesConstructorValues()
    {
        TranscriptGenerationContext original = CreateMinimalContext(sourceLanguage: "en");
        TranscriptGenerationContext modified = original with
        {
            SpeechRegions = [new SpeechRegion(0, 0.0, 5.0)],
        };

        Assert.Equal("en", modified.SourceLanguage);
        Assert.Equal(original.Project, modified.Project);
        Assert.Equal(original.MediaAsset, modified.MediaAsset);
    }

    [Fact]
    public void MultipleWithExpressions_ProduceIndependentInstances()
    {
        TranscriptGenerationContext original = CreateMinimalContext();

        TranscriptGenerationContext v1 = original with
        {
            SpeechRegions = [new SpeechRegion(0, 0.0, 5.0)],
            VadStageRunId = Guid.NewGuid(),
        };

        TranscriptGenerationContext v2 = original with
        {
            SpeechRegions = [new SpeechRegion(0, 0.0, 10.0), new SpeechRegion(1, 11.0, 20.0)],
            VadStageRunId = Guid.NewGuid(),
        };

        Assert.Single(v1.SpeechRegions);
        Assert.Equal(2, v2.SpeechRegions.Count);
        Assert.NotEqual(v1.VadStageRunId, v2.VadStageRunId);
        Assert.Empty(original.SpeechRegions);
    }

    private static TranscriptGenerationContext CreateMinimalContext(
        IReadOnlyList<SpeechRegion>? speechRegions = null,
        string? sourceLanguage = null)
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "Demo", now, now);
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            1.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var audioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.NormalizedAudio,
            "artifacts/audio.wav",
            "audio-hash",
            100,
            1.0d,
            16000,
            1,
            now);

        var context = new TranscriptGenerationContext(
            project,
            mediaAsset,
            audioArtifact,
            TranscriptAudioRoutingPlan.Raw(audioArtifact, SpeechAudioSourceKind.FullMix),
            enableSpeakerDiarization: false,
            sourceLanguage: sourceLanguage);

        if (speechRegions is not null)
        {
            context = context with { SpeechRegions = speechRegions };
        }

        return context;
    }

    /// <summary>
    /// Uses reflection to verify the internal backing store for SpeechRegions is a SpeechRegion[].
    /// This is a white-box test that documents the defensive-copy contract.
    /// </summary>
    private static object GetSpeechRegionsBacking(TranscriptGenerationContext context)
    {
        // The record stores auto-property values in compiler-generated <PropertyName>k__BackingField.
        // We use this only to verify the internal type — not to bypass immutability.
        var field = typeof(TranscriptGenerationContext)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(f => f.Name.Contains("speechRegions", StringComparison.OrdinalIgnoreCase));
        return field.GetValue(context)!;
    }
}
