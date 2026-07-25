using Trackdub.Inference.Onnx.ForcedAlignment;

namespace Trackdub.Inference.Onnx.Tests.ForcedAlignment;

public sealed class CtcViterbiAlignerTests
{
    [Fact]
    public void Align_EmptyPhonemeSequence_ReturnsEmpty()
    {
        // Uniform log-probs over 10 frames, 4-token vocab
        float[] logProbs = MakeUniformLogProbs(frameCount: 10, vocabSize: 4);

        var result = CtcViterbiAligner.Align(logProbs, 10, 4, ReadOnlySpan<int>.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void Align_SinglePhoneme_StartAndEndWithinFrameRange()
    {
        const int frameCount = 8;
        const int vocabSize = 4;
        const int phonemeIndex = 2;

        float[] logProbs = MakeUniformLogProbs(frameCount, vocabSize);
        int[] phonemes = [phonemeIndex];

        var result = CtcViterbiAligner.Align(logProbs, frameCount, vocabSize, phonemes);

        Assert.Single(result);
        Assert.InRange(result[0].StartFrame, 0, frameCount - 1);
        Assert.InRange(result[0].EndFrame, 0, frameCount - 1);
        Assert.True(result[0].StartFrame <= result[0].EndFrame);
    }

    [Fact]
    public void Align_MultiplePhonemes_StartFramesMonotonicallyNonDecreasing()
    {
        const int frameCount = 20;
        const int vocabSize = 5;
        int[] phonemes = [1, 2, 3, 4];

        // Give each phoneme a "hot" frame so the aligner has signal to spread them out
        float[] logProbs = MakePeakedLogProbs(frameCount, vocabSize, phonemes);

        var result = CtcViterbiAligner.Align(logProbs, frameCount, vocabSize, phonemes);

        Assert.Equal(phonemes.Length, result.Length);
        for (int i = 1; i < result.Length; i++)
        {
            Assert.True(result[i].StartFrame >= result[i - 1].StartFrame,
                $"StartFrame[{i}]={result[i].StartFrame} < StartFrame[{i - 1}]={result[i - 1].StartFrame}");
        }
    }

    [Fact]
    public void Align_ResultLengthEqualsPhonemeCount()
    {
        const int frameCount = 12;
        const int vocabSize = 6;
        int[] phonemes = [1, 2, 3, 4];

        float[] logProbs = MakeUniformLogProbs(frameCount, vocabSize);

        var result = CtcViterbiAligner.Align(logProbs, frameCount, vocabSize, phonemes);

        Assert.Equal(phonemes.Length, result.Length);
    }

    [Fact]
    public void Align_SequenceLongerThanFrameCount_ReturnsEmpty()
    {
        const int frameCount = 2;
        const int vocabSize = 4;
        int[] phonemes = [1, 2, 3, 1]; // 4 phonemes > 2 frames

        float[] logProbs = MakeUniformLogProbs(frameCount, vocabSize);

        var result = CtcViterbiAligner.Align(logProbs, frameCount, vocabSize, phonemes);

        Assert.Empty(result);
    }

    [Fact]
    public void Align_ZeroFrames_ReturnsEmpty()
    {
        float[] logProbs = [];
        int[] phonemes = [1];

        var result = CtcViterbiAligner.Align(logProbs, 0, 4, phonemes);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Align_BlankTokenIndexOutOfRange_ReturnsEmpty(int blankTokenIndex)
    {
        const int frameCount = 4;
        const int vocabSize = 4;
        float[] logProbs = MakeUniformLogProbs(frameCount, vocabSize);

        var result = CtcViterbiAligner.Align(
            logProbs,
            frameCount,
            vocabSize,
            phonemeSequence: [1, 2],
            blankTokenIndex: blankTokenIndex);

        Assert.Empty(result);
    }

    [Fact]
    public void Align_PhonemeSequenceContainsBlankToken_ReturnsEmpty()
    {
        const int frameCount = 4;
        const int vocabSize = 4;
        const int blankTokenIndex = 0;
        float[] logProbs = MakeUniformLogProbs(frameCount, vocabSize);

        var result = CtcViterbiAligner.Align(
            logProbs,
            frameCount,
            vocabSize,
            phonemeSequence: [1, blankTokenIndex, 2],
            blankTokenIndex: blankTokenIndex);

        Assert.Empty(result);
    }

    [Fact]
    public void Align_LeavesSilentFramesBlank_AndScoresOnlyPhonemeFrames()
    {
        const int blank = 0;
        const int phonemeA = 1;
        const int phonemeB = 2;
        const int vocabSize = 3;
        float[] logProbs =
        [
            -0.01f, -8f, -8f,
            -8f, -0.02f, -8f,
            -0.01f, -8f, -8f,
            -8f, -8f, -0.03f,
            -0.01f, -8f, -8f,
        ];

        var result = CtcViterbiAligner.Align(
            logProbs,
            frameCount: 5,
            vocabSize,
            phonemeSequence: [phonemeA, phonemeB],
            blankTokenIndex: blank);

        Assert.Equal(2, result.Length);
        Assert.Equal((1, 1), (result[0].StartFrame, result[0].EndFrame));
        Assert.Equal((3, 3), (result[1].StartFrame, result[1].EndFrame));
        Assert.True(result[0].LogProb > -0.1f);
        Assert.True(result[1].LogProb > -0.1f);
    }

    [Fact]
    public void Align_RequiresBlankBetweenRepeatedPhonemes()
    {
        const int blank = 0;
        const int phoneme = 1;
        const int vocabSize = 2;
        float[] logProbs =
        [
            -0.01f, -8f,
            -8f, -0.02f,
            -0.01f, -8f,
            -8f, -0.03f,
            -0.01f, -8f,
        ];

        var result = CtcViterbiAligner.Align(
            logProbs,
            frameCount: 5,
            vocabSize,
            phonemeSequence: [phoneme, phoneme],
            blankTokenIndex: blank);

        Assert.Equal(2, result.Length);
        Assert.Equal((1, 1), (result[0].StartFrame, result[0].EndFrame));
        Assert.Equal((3, 3), (result[1].StartFrame, result[1].EndFrame));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static float[] MakeUniformLogProbs(int frameCount, int vocabSize)
    {
        float uniform = MathF.Log(1f / vocabSize);
        float[] logProbs = new float[frameCount * vocabSize];
        logProbs.AsSpan().Fill(uniform);
        return logProbs;
    }

    /// <summary>
    /// Returns log-probs where each phoneme token has a high score at evenly spaced frames,
    /// giving the aligner clear signal to assign each phoneme its own region.
    /// </summary>
    private static float[] MakePeakedLogProbs(int frameCount, int vocabSize, int[] phonemes)
    {
        float[] logProbs = new float[frameCount * vocabSize];
        float background = MathF.Log(0.01f / vocabSize);
        logProbs.AsSpan().Fill(background);

        int spacing = Math.Max(1, frameCount / phonemes.Length);
        for (int p = 0; p < phonemes.Length; p++)
        {
            int peakFrame = Math.Min(p * spacing, frameCount - 1);
            logProbs[peakFrame * vocabSize + phonemes[p]] = MathF.Log(0.9f);
        }

        return logProbs;
    }
}
