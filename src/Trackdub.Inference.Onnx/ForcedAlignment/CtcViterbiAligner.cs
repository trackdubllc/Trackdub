namespace Trackdub.Inference.Onnx.ForcedAlignment;

/// <summary>
/// Pure managed blank-aware CTC forced-alignment DP.
/// No I/O or model dependency - only the log-probability matrix and label sequence are needed.
/// </summary>
public static class CtcViterbiAligner
{
    /// <summary>
    /// Aligns a phoneme sequence to per-frame log-probability vectors using a Viterbi CTC trellis.
    /// </summary>
    /// <param name="logProbs">
    /// Flattened row-major matrix of shape [<paramref name="frameCount"/>, <paramref name="vocabSize"/>].
    /// <c>logProbs[t * vocabSize + c]</c> is the log-probability of token <c>c</c> at frame <c>t</c>.
    /// </param>
    /// <param name="frameCount">Number of time steps.</param>
    /// <param name="vocabSize">Vocabulary size (number of tokens).</param>
    /// <param name="phonemeSequence">Non-blank token indices to align.</param>
    /// <param name="blankTokenIndex">Index of the CTC blank token.</param>
    /// <returns>
    /// One <c>(StartFrame, EndFrame, LogProb)</c> tuple per phoneme. <c>LogProb</c> is the
    /// average score for frames assigned to that phoneme, excluding CTC blanks. Returns an empty
    /// array when no valid CTC path reaches every requested phoneme.
    /// </returns>
    public static (int StartFrame, int EndFrame, float LogProb)[] Align(
        ReadOnlySpan<float> logProbs,
        int frameCount,
        int vocabSize,
        ReadOnlySpan<int> phonemeSequence,
        int blankTokenIndex = 0)
    {
        int phonemeCount = phonemeSequence.Length;
        if (phonemeCount == 0 || frameCount <= 0 || vocabSize <= 0)
            return [];

        if (logProbs.Length < frameCount * vocabSize || blankTokenIndex < 0 || blankTokenIndex >= vocabSize)
            return [];

        for (int i = 0; i < phonemeCount; i++)
        {
            if (phonemeSequence[i] < 0 || phonemeSequence[i] >= vocabSize || phonemeSequence[i] == blankTokenIndex)
                return [];
        }

        // The extended target is blank, phoneme 0, blank, phoneme 1, ..., blank. This permits
        // silence and transitions to remain blank instead of being scored as a phoneme.
        int stateCount = checked((phonemeCount * 2) + 1);
        int[] stateTokens = new int[stateCount];
        for (int state = 0; state < stateCount; state++)
            stateTokens[state] = (state & 1) == 0 ? blankTokenIndex : phonemeSequence[state / 2];

        float[] trellis = new float[frameCount * stateCount];
        int[] predecessors = new int[frameCount * stateCount];
        trellis.AsSpan().Fill(float.NegativeInfinity);
        predecessors.AsSpan().Fill(-1);

        // At the first frame, CTC can either remain blank or emit the first phoneme.
        trellis[0] = logProbs[blankTokenIndex];
        trellis[1] = logProbs[phonemeSequence[0]];

        for (int frame = 1; frame < frameCount; frame++)
        {
            int frameBase = frame * stateCount;
            int previousFrameBase = (frame - 1) * stateCount;
            int emissionFrameBase = frame * vocabSize;

            for (int state = 0; state < stateCount; state++)
            {
                float bestScore = trellis[previousFrameBase + state];
                int predecessor = state;

                if (state > 0 && trellis[previousFrameBase + state - 1] > bestScore)
                {
                    bestScore = trellis[previousFrameBase + state - 1];
                    predecessor = state - 1;
                }

                // A direct skip over blank is legal only for a distinct phoneme. Repeated labels
                // must consume an intervening blank, as required by CTC.
                if (state > 1 && (state & 1) == 1 &&
                    stateTokens[state] != stateTokens[state - 2] &&
                    trellis[previousFrameBase + state - 2] > bestScore)
                {
                    bestScore = trellis[previousFrameBase + state - 2];
                    predecessor = state - 2;
                }

                if (float.IsNegativeInfinity(bestScore))
                    continue;

                trellis[frameBase + state] = bestScore + logProbs[emissionFrameBase + stateTokens[state]];
                predecessors[frameBase + state] = predecessor;
            }
        }

        int finalBlankState = stateCount - 1;
        int finalPhonemeState = stateCount - 2;
        int finalFrameBase = (frameCount - 1) * stateCount;
        int currentState = trellis[finalFrameBase + finalPhonemeState] >= trellis[finalFrameBase + finalBlankState]
            ? finalPhonemeState
            : finalBlankState;
        if (float.IsNegativeInfinity(trellis[finalFrameBase + currentState]))
            return [];

        int[] path = new int[frameCount];
        for (int frame = frameCount - 1; frame >= 0; frame--)
        {
            path[frame] = currentState;
            if (frame > 0)
                currentState = predecessors[(frame * stateCount) + currentState];
        }

        var result = new (int StartFrame, int EndFrame, float LogProb)[phonemeCount];
        for (int phoneme = 0; phoneme < phonemeCount; phoneme++)
        {
            int start = -1;
            int end = -1;
            double logProbSum = 0d;
            int count = 0;
            int phonemeState = (phoneme * 2) + 1;

            for (int frame = 0; frame < frameCount; frame++)
            {
                if (path[frame] != phonemeState)
                    continue;

                if (start == -1)
                    start = frame;
                end = frame;
                logProbSum += logProbs[(frame * vocabSize) + phonemeSequence[phoneme]];
                count++;
            }

            if (start == -1)
                return [];

            result[phoneme] = (start, end, (float)(logProbSum / count));
        }

        return result;
    }
}
