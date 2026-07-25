using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.DeepFilterNet;

namespace Trackdub.Composition.DeepFilterNet;

internal static class DeepFilterNetChunkedEnhancer
{
    // ~6 s of frames per ONNX inference; overlap-add hides chunk seams.
    internal const int ChunkFrames = 600;
    internal const int OverlapFrames = 50;

    private static int ChunkSamples => ChunkFrames * DeepFilterNetSignalProcessor.HopSize;
    private static int OverlapSamples => OverlapFrames * DeepFilterNetSignalProcessor.HopSize;
    private static int LookbackSamples => DeepFilterNetSignalProcessor.FftSize;

    public static async Task<float[]> EnhanceAsync(
        IAudioSamples audio,
        DeepFilterNetModelSessions sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(sessions);

        long totalSamplesLong = audio.SampleFrameCount;
        if (totalSamplesLong < 0)
        {
            throw new InvalidOperationException(
                $"DeepFilterNet audio source reported an invalid sample count: {totalSamplesLong}.");
        }

        if (totalSamplesLong == 0)
        {
            return [];
        }

        int totalSamples = checked((int)totalSamplesLong);
        var output = new float[totalSamples];
        var normWeights = new float[totalSamples];
        DeepFilterNetFeatureNormState normState = DeepFilterNetFeatureNormState.CreateInitial();
        float[] fadeIn = DeepFilterNetSignalProcessor.BuildLinearRamp(OverlapSamples, rising: true);
        float[] fadeOut = DeepFilterNetSignalProcessor.BuildLinearRamp(OverlapSamples, rising: false);

        int chunkStart = 0;
        while (chunkStart < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int chunkEnd = Math.Min(chunkStart + ChunkSamples, totalSamples);
            int chunkLen = chunkEnd - chunkStart;
            int readStart = Math.Max(0, chunkStart - LookbackSamples);
            int readLen = chunkEnd - readStart;

            var pcm = new float[readLen];
            audio.ReadMonoSamples(readStart, pcm);

            DeepFilterNetSignalProcessor.ComputeFeatures(
                pcm,
                normState,
                out float[,,,] featErb,
                out float[,,,] featSpec,
                out MathNet.Numerics.Complex32[,] stftFrames);

            int numFrames = featErb.GetLength(2);
            (float[,,,] erbGains, float[,,,,] dfCoefs) = DeepFilterNetOnnxInference.Run(
                sessions, featErb, featSpec, numFrames);

            float[] chunkOut = DeepFilterNetSignalProcessor.Synthesize(
                stftFrames, erbGains, dfCoefs, readLen);

            int chunkOutOffset = chunkStart - readStart;
            OverlapAddSegment(
                output,
                normWeights,
                chunkOut,
                chunkOutOffset,
                chunkLen,
                chunkStart,
                fadeIn,
                fadeOut);

            chunkStart += ChunkSamples - OverlapSamples;
        }

        NormalizeByWeights(output, normWeights);
        return output;
    }

    private static void OverlapAddSegment(
        float[] output,
        float[] normWeights,
        float[] chunkOut,
        int chunkOutOffset,
        int chunkLen,
        int globalStart,
        float[] fadeIn,
        float[] fadeOut)
    {
        for (int i = 0; i < chunkLen; i++)
        {
            int globalIdx = globalStart + i;
            int chunkIdx = chunkOutOffset + i;
            if (chunkIdx < 0 || chunkIdx >= chunkOut.Length)
            {
                continue;
            }

            float w = 1f;
            if (i < OverlapSamples)
            {
                w = fadeIn[i];
            }
            else if (i >= chunkLen - OverlapSamples)
            {
                int fadeIdx = i - (chunkLen - OverlapSamples);
                w = fadeIdx < OverlapSamples ? fadeOut[fadeIdx] : 0f;
            }

            output[globalIdx] += chunkOut[chunkIdx] * w;
            normWeights[globalIdx] += w;
        }
    }

    private static void NormalizeByWeights(float[] output, float[] normWeights)
    {
        const float eps = 1e-6f;
        for (int i = 0; i < output.Length; i++)
        {
            if (normWeights[i] > eps)
            {
                output[i] /= normWeights[i];
            }
        }
    }
}
