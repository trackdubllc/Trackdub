namespace Trackdub.Inference.Onnx.SortFormer;

/// <summary>
/// Port of NeMo <c>SortformerModules._compress_spkcache</c> (inference path, mean silence embedding).
/// Streaming Sortformer labels speakers by arrival order in the speaker cache, so the cache must keep
/// representative frames for every speaker heard so far. Keeping only the most recent frames lets an
/// early speaker age out, after which the next speaker in the cache inherits index 0.
/// </summary>
internal static class SortFormerSpeakerCacheCompressor
{
    internal const int SilenceFramesPerSpeaker = 3;
    internal const float SilenceThreshold = 0.2f;
    private const float PredScoreThreshold = 0.25f;
    private const float ScoresBoostLatest = 0.05f;
    private const float StrongBoostRate = 0.75f;
    private const float WeakBoostRate = 1.5f;
    private const float MinPosScoresRate = 0.5f;
    private static readonly float LogHalf = MathF.Log(0.5f);

    /// <summary>
    /// Selects <paramref name="cacheFrames"/> frames out of <paramref name="frameCount"/> candidates.
    /// Output frames are grouped by speaker; within a speaker the original order is kept. Slots that
    /// resolve to no usable frame hold the mean silence embedding and zero predictions.
    /// </summary>
    public static (float[] Embeddings, float[] Predictions) Compress(
        float[] embeddings,
        float[] predictions,
        int frameCount,
        int speakerCount,
        int embeddingDimension,
        int cacheFrames,
        float[] meanSilenceEmbedding)
    {
        int perSpeaker = (cacheFrames / speakerCount) - SilenceFramesPerSpeaker;
        int strongBoost = (int)MathF.Floor(perSpeaker * StrongBoostRate);
        int weakBoost = (int)MathF.Floor(perSpeaker * WeakBoostRate);
        int minPositive = (int)MathF.Floor(perSpeaker * MinPosScoresRate);

        float[] scores = ComputeScores(predictions, frameCount, speakerCount, minPositive);
        for (int t = cacheFrames; t < frameCount; t++)
        {
            for (int s = 0; s < speakerCount; s++)
            {
                scores[(t * speakerCount) + s] += ScoresBoostLatest;
            }
        }

        BoostTopK(scores, frameCount, speakerCount, strongBoost, scaleFactor: 2f);
        BoostTopK(scores, frameCount, speakerCount, weakBoost, scaleFactor: 1f);

        // Candidates are flattened speaker-major over frameCount + silence padding frames; the padding
        // frames score +inf so each speaker block reserves SilenceFramesPerSpeaker silence slots.
        int paddedFrames = frameCount + SilenceFramesPerSpeaker;
        var candidates = new (float Score, int Index)[speakerCount * paddedFrames];
        for (int s = 0; s < speakerCount; s++)
        {
            for (int t = 0; t < paddedFrames; t++)
            {
                float score = t < frameCount ? scores[(t * speakerCount) + s] : float.PositiveInfinity;
                candidates[(s * paddedFrames) + t] = (score, (s * paddedFrames) + t);
            }
        }

        int[] selected = candidates
            .OrderByDescending(static c => c.Score)
            .ThenBy(static c => c.Index)
            .Take(cacheFrames)
            .Select(static c => float.IsNegativeInfinity(c.Score) ? int.MaxValue : c.Index)
            .Order()
            .ToArray();

        var cacheEmbeddings = new float[cacheFrames * embeddingDimension];
        var cachePredictions = new float[cacheFrames * speakerCount];
        for (int i = 0; i < selected.Length; i++)
        {
            int frame = selected[i] == int.MaxValue ? -1 : selected[i] % paddedFrames;
            if (frame < 0 || frame >= frameCount)
            {
                Array.Copy(meanSilenceEmbedding, 0, cacheEmbeddings, i * embeddingDimension, embeddingDimension);
                continue;
            }

            Array.Copy(embeddings, frame * embeddingDimension, cacheEmbeddings, i * embeddingDimension, embeddingDimension);
            Array.Copy(predictions, frame * speakerCount, cachePredictions, i * speakerCount, speakerCount);
        }

        return (cacheEmbeddings, cachePredictions);
    }

    /// <summary>NeMo <c>_get_silence_profile</c>: running mean of embeddings whose summed activity is below threshold.</summary>
    public static int UpdateSilenceProfile(
        float[] meanSilenceEmbedding,
        int silenceFrameCount,
        float[] embeddings,
        float[] predictions,
        int frameCount,
        int speakerCount,
        int embeddingDimension)
    {
        var sum = new double[embeddingDimension];
        int newFrames = 0;
        for (int t = 0; t < frameCount; t++)
        {
            float activity = 0f;
            for (int s = 0; s < speakerCount; s++)
            {
                activity += predictions[(t * speakerCount) + s];
            }

            if (activity >= SilenceThreshold)
            {
                continue;
            }

            newFrames++;
            for (int d = 0; d < embeddingDimension; d++)
            {
                sum[d] += embeddings[(t * embeddingDimension) + d];
            }
        }

        if (newFrames == 0)
        {
            return silenceFrameCount;
        }

        int total = silenceFrameCount + newFrames;
        for (int d = 0; d < embeddingDimension; d++)
        {
            meanSilenceEmbedding[d] = (float)(((meanSilenceEmbedding[d] * (double)silenceFrameCount) + sum[d]) / total);
        }

        return total;
    }

    // NeMo _get_log_pred_scores + _disable_low_scores: high for confident single-speaker frames,
    // -inf for non-speech, and -inf for overlapped frames once a speaker has enough clean frames.
    private static float[] ComputeScores(float[] predictions, int frameCount, int speakerCount, int minPositive)
    {
        var scores = new float[frameCount * speakerCount];
        for (int t = 0; t < frameCount; t++)
        {
            float logNotSum = 0f;
            for (int s = 0; s < speakerCount; s++)
            {
                logNotSum += MathF.Log(MathF.Max(1f - predictions[(t * speakerCount) + s], PredScoreThreshold));
            }

            for (int s = 0; s < speakerCount; s++)
            {
                float p = predictions[(t * speakerCount) + s];
                float score = MathF.Log(MathF.Max(p, PredScoreThreshold))
                    - MathF.Log(MathF.Max(1f - p, PredScoreThreshold))
                    + logNotSum
                    - LogHalf;
                scores[(t * speakerCount) + s] = p > 0.5f ? score : float.NegativeInfinity;
            }
        }

        for (int s = 0; s < speakerCount; s++)
        {
            int positive = 0;
            for (int t = 0; t < frameCount; t++)
            {
                if (scores[(t * speakerCount) + s] > 0f)
                {
                    positive++;
                }
            }

            if (positive < minPositive)
            {
                continue;
            }

            for (int t = 0; t < frameCount; t++)
            {
                int i = (t * speakerCount) + s;
                if (scores[i] <= 0f)
                {
                    scores[i] = float.NegativeInfinity;
                }
            }
        }

        return scores;
    }

    private static void BoostTopK(float[] scores, int frameCount, int speakerCount, int k, float scaleFactor)
    {
        int take = Math.Min(k, frameCount);
        float boost = -scaleFactor * LogHalf;
        for (int s = 0; s < speakerCount; s++)
        {
            IEnumerable<int> top = Enumerable.Range(0, frameCount)
                .OrderByDescending(t => scores[(t * speakerCount) + s])
                .ThenBy(static t => t)
                .Take(take);
            foreach (int t in top)
            {
                scores[(t * speakerCount) + s] += boost;
            }
        }
    }
}
