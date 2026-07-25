namespace Trackdub.Inference.Onnx.CosyVoice;

internal static class CosyVoiceRasSampler
{
    public static int Sample(float[] logProbs, IReadOnlyList<int> decodedTokens, int topK = 25, float topP = 0.8f, int winSize = 10, float tauR = 0.1f)
    {
        int topId = NucleusSample(logProbs, topK, topP);
        int start = Math.Max(0, decodedTokens.Count - winSize);
        int repeats = 0;
        for (int i = start; i < decodedTokens.Count; i++)
        {
            if (decodedTokens[i] == topId)
            {
                repeats++;
            }
        }

        if (repeats >= winSize * tauR)
        {
            logProbs = logProbs.ToArray();
            logProbs[topId] = float.NegativeInfinity;
            topId = RandomSample(logProbs);
        }

        return topId;
    }

    private static int NucleusSample(float[] logProbs, int topK, float topP)
    {
        int vocab = logProbs.Length;
        var probs = new float[vocab];
        float max = logProbs.Max();
        double sum = 0d;
        for (int i = 0; i < vocab; i++)
        {
            probs[i] = (float)Math.Exp(logProbs[i] - max);
            sum += probs[i];
        }

        for (int i = 0; i < vocab; i++)
        {
            probs[i] = (float)(probs[i] / sum);
        }

        var indices = Enumerable.Range(0, vocab).OrderByDescending(i => probs[i]).ToArray();
        var selected = new List<int>();
        double cumulative = 0d;
        foreach (int index in indices)
        {
            if (cumulative < topP && selected.Count < topK)
            {
                cumulative += probs[index];
                selected.Add(index);
            }
            else
            {
                break;
            }
        }

        double sampleSum = selected.Sum(i => probs[i]);
        double roll = Random.Shared.NextDouble() * sampleSum;
        double acc = 0d;
        foreach (int index in selected)
        {
            acc += probs[index];
            if (roll <= acc)
            {
                return index;
            }
        }

        return selected[^1];
    }

    private static int RandomSample(float[] logProbs)
    {
        int vocab = logProbs.Length;
        float max = logProbs.Where(static v => !float.IsNegativeInfinity(v)).DefaultIfEmpty(0f).Max();
        double sum = 0d;
        var probs = new double[vocab];
        for (int i = 0; i < vocab; i++)
        {
            probs[i] = float.IsNegativeInfinity(logProbs[i]) ? 0d : Math.Exp(logProbs[i] - max);
            sum += probs[i];
        }

        double roll = Random.Shared.NextDouble() * sum;
        double acc = 0d;
        for (int i = 0; i < vocab; i++)
        {
            acc += probs[i];
            if (roll <= acc)
            {
                return i;
            }
        }

        return vocab - 1;
    }
}
