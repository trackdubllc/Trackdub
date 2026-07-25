using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubDubbingEngineStageOrderTests
{
    [Fact]
    public void ResolveStageOrder_Unfiltered_ExcludesLipStages()
    {
        string[] order = InvokeResolve(null);
        Assert.Equal(
            [
                StageNames.Separation,
                StageNames.Vad,
                StageNames.Diarization,
                StageNames.Asr,
                StageNames.Translation,
                StageNames.Tts,
                StageNames.Export,
            ],
            order);
        Assert.DoesNotContain(StageNames.LipSync, order);
        Assert.DoesNotContain(StageNames.LipSynthesis, order);
    }

    [Fact]
    public void ResolveStageOrder_FilterIncludesLipStages_PreservesExtendedOrder()
    {
        string[] order = InvokeResolve(
        [
            StageNames.LipSynthesis,
            StageNames.Tts,
            StageNames.LipSync,
            StageNames.Export,
        ]);

        Assert.Equal(
            [
                StageNames.Tts,
                StageNames.LipSync,
                StageNames.Export,
                StageNames.LipSynthesis,
            ],
            order);
    }

    [Fact]
    public void ResolveStageOrder_LipSyncAlone_IsDispatched()
    {
        string[] order = InvokeResolve([StageNames.LipSync]);
        Assert.Equal([StageNames.LipSync], order);
    }

    private static string[] InvokeResolve(IReadOnlyList<string>? filter)
    {
        var method = typeof(TrackdubDubbingEngine).GetMethod(
            "ResolveStageOrder",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(TrackdubDubbingEngine), "ResolveStageOrder");

        return (string[])method.Invoke(null, [filter])!;
    }
}
