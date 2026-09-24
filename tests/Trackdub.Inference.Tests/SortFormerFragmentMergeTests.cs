using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.SortFormer;
using Xunit;

namespace Trackdub.Inference.Tests;

public sealed class SortFormerFragmentMergeTests
{
    [Fact]
    public void MergeFragmentSpeakers_RelabelsTransientSlotToIncomingSpeaker()
    {
        List<DiarizedSpeakerTurn> turns =
        [
            new("spk_1", 30.7, 93.9),
            new("spk_2", 94.1, 96.7),
            new("spk_3", 99.1, 106.0),
            new("spk_3", 113.3, 185.2),
        ];

        List<DiarizedSpeakerTurn> merged = SortFormerDiarizationEngine.MergeFragmentSpeakers(turns);

        Assert.Equal(["spk_1", "spk_3", "spk_3", "spk_3"], merged.Select(static t => t.SpeakerKey));
        Assert.Equal(94.1, merged[1].StartSeconds);
    }

    [Fact]
    public void MergeFragmentSpeakers_TrailingFragmentFallsBackToPreviousSpeaker()
    {
        List<DiarizedSpeakerTurn> turns = [new("spk_0", 0, 20), new("spk_1", 21, 22)];

        List<DiarizedSpeakerTurn> merged = SortFormerDiarizationEngine.MergeFragmentSpeakers(turns);

        Assert.Equal("spk_0", merged[1].SpeakerKey);
    }

    [Fact]
    public void MergeFragmentSpeakers_KeepsShortTurnsOfEstablishedSpeakers()
    {
        List<DiarizedSpeakerTurn> turns =
        [
            new("spk_0", 0, 10),
            new("spk_1", 10.5, 11.0),
            new("spk_0", 11.5, 20),
            new("spk_1", 21, 30),
        ];

        List<DiarizedSpeakerTurn> merged = SortFormerDiarizationEngine.MergeFragmentSpeakers(turns);

        Assert.Equal(turns.Select(static t => t.SpeakerKey), merged.Select(static t => t.SpeakerKey));
    }
}
