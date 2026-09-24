using Trackdub.Inference.Onnx.SortFormer;
using Xunit;

namespace Trackdub.Inference.Tests;

public sealed class SortFormerSpeakerCacheCompressorTests
{
    private const int Speakers = 4;
    private const int Dim = 2;
    private const int CacheFrames = 188;

    [Fact]
    public void Compress_KeepsEarlySpeakerFramesInsteadOfOnlyTheLatest()
    {
        // 150 frames of speaker 0, then 150 of speaker 1: truncating to the last 188 would keep
        // only 38 speaker-0 frames. Compression must reserve a fair share for speaker 0 and keep
        // speaker 0's frames ahead of speaker 1's (arrival order).
        const int frames = 300;
        (float[] emb, float[] preds) = BuildTwoSpeakerSequence(frames, switchAt: 150);

        (float[] cacheEmb, float[] cachePreds) = SortFormerSpeakerCacheCompressor.Compress(
            emb, preds, frames, Speakers, Dim, CacheFrames, meanSilenceEmbedding: [-1f, -1f]);

        int speaker0 = 0, speaker1 = 0, lastSpeaker = 0;
        for (int i = 0; i < CacheFrames; i++)
        {
            int speaker = cachePreds[(i * Speakers) + 0] > 0.5f ? 0 : cachePreds[(i * Speakers) + 1] > 0.5f ? 1 : -1;
            if (speaker < 0)
            {
                continue;
            }

            Assert.True(speaker >= lastSpeaker, "Cache frames must be grouped by speaker in arrival order.");
            lastSpeaker = speaker;
            if (speaker == 0) speaker0++; else speaker1++;
        }

        Assert.True(speaker0 >= 44, $"Speaker 0 kept only {speaker0} frames.");
        Assert.True(speaker1 >= 44, $"Speaker 1 kept only {speaker1} frames.");
    }

    [Fact]
    public void Compress_FillsUnusedSlotsWithMeanSilenceEmbedding()
    {
        const int frames = 200;
        (float[] emb, float[] preds) = BuildTwoSpeakerSequence(frames, switchAt: frames);

        (float[] cacheEmb, float[] cachePreds) = SortFormerSpeakerCacheCompressor.Compress(
            emb, preds, frames, Speakers, Dim, CacheFrames, meanSilenceEmbedding: [-1f, -1f]);

        int silenceSlots = 0;
        for (int i = 0; i < CacheFrames; i++)
        {
            if (cacheEmb[i * Dim] == -1f)
            {
                silenceSlots++;
                Assert.All(cachePreds.AsSpan(i * Speakers, Speakers).ToArray(), static p => Assert.Equal(0f, p));
            }
        }

        Assert.True(silenceSlots > 0);
    }

    [Fact]
    public void UpdateSilenceProfile_AveragesOnlyLowActivityFrames()
    {
        var mean = new float[Dim];
        float[] emb = [2f, 2f, 10f, 10f, 4f, 4f];
        float[] preds =
        [
            0.05f, 0f, 0f, 0f,
            0.9f, 0f, 0f, 0f,
            0.1f, 0f, 0f, 0f,
        ];

        int count = SortFormerSpeakerCacheCompressor.UpdateSilenceProfile(mean, 0, emb, preds, 3, Speakers, Dim);

        Assert.Equal(2, count);
        Assert.Equal(3f, mean[0], 5);
    }

    private static (float[] Embeddings, float[] Predictions) BuildTwoSpeakerSequence(int frames, int switchAt)
    {
        var emb = new float[frames * Dim];
        var preds = new float[frames * Speakers];
        for (int t = 0; t < frames; t++)
        {
            int speaker = t < switchAt ? 0 : 1;
            emb[t * Dim] = t;
            emb[(t * Dim) + 1] = speaker;
            preds[(t * Speakers) + speaker] = 0.95f;
        }

        return (emb, preds);
    }
}
