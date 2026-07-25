using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.CosyVoice;

internal static class CosyVoiceFlowMatching
{
    public static float[] Solve(
        InferenceSession estimator,
        float[] mu,
        int melLength,
        float[] speakerEmbedding,
        float[] cond,
        CancellationToken cancellationToken)
    {
        int channels = CosyVoiceConstants.MelBins;
        var x = new float[channels * melLength];
        var random = new Random(0);
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = (float)(random.NextDouble() * 2d - 1d);
        }

        Span<float> tSpan = stackalloc float[CosyVoiceConstants.CfmSteps + 1];
        for (int i = 0; i < tSpan.Length; i++)
        {
            tSpan[i] = i / (float)CosyVoiceConstants.CfmSteps;
        }

        for (int i = 0; i < tSpan.Length; i++)
        {
            tSpan[i] = 1f - (float)Math.Cos(tSpan[i] * 0.5d * Math.PI);
        }

        float dt = tSpan[1] - tSpan[0];
        float t = tSpan[0];
        var mask = new float[melLength];
        Array.Fill(mask, 1f);

        for (int step = 1; step < tSpan.Length; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] dPhi = RunEstimator(estimator, x, mask, mu, melLength, t, speakerEmbedding, cond);
            for (int i = 0; i < x.Length; i++)
            {
                x[i] += dt * dPhi[i];
            }

            t += dt;
            if (step < tSpan.Length - 1)
            {
                dt = tSpan[step + 1] - tSpan[step];
            }
        }

        return x;
    }

    private static float[] RunEstimator(
        InferenceSession estimator,
        float[] x,
        float[] mask,
        float[] mu,
        int melLength,
        float t,
        float[] speakerEmbedding,
        float[] cond)
    {
        int channels = CosyVoiceConstants.MelBins;
        int slice = channels * melLength;
        var xBatch = new float[2 * slice];
        var maskBatch = new float[2 * melLength];
        var muBatch = new float[2 * slice];
        var condBatch = new float[2 * slice];
        var spkBatch = new float[2 * channels];

        Array.Copy(x, 0, xBatch, 0, slice);
        Array.Copy(x, 0, xBatch, slice, slice);
        Array.Copy(mask, 0, maskBatch, 0, melLength);
        Array.Copy(mask, 0, maskBatch, melLength, melLength);
        Array.Copy(mu, 0, muBatch, 0, slice);
        Array.Copy(cond, 0, condBatch, 0, slice);
        Array.Copy(speakerEmbedding, 0, spkBatch, 0, channels);

        using var inputs = new OnnxInputBatch();
        inputs.Add(NamedOnnxValue.CreateFromTensor("x", new DenseTensor<float>(xBatch, [2, channels, melLength])));
        inputs.Add(NamedOnnxValue.CreateFromTensor("mask", new DenseTensor<float>(maskBatch, [2, 1, melLength])));
        inputs.Add(NamedOnnxValue.CreateFromTensor("mu", new DenseTensor<float>(muBatch, [2, channels, melLength])));
        inputs.Add(NamedOnnxValue.CreateFromTensor("t", new DenseTensor<float>(new[] { t, t }, [2])));
        inputs.Add(NamedOnnxValue.CreateFromTensor("spks", new DenseTensor<float>(spkBatch, [2, channels])));
        inputs.Add(NamedOnnxValue.CreateFromTensor("cond", new DenseTensor<float>(condBatch, [2, channels, melLength])));

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = estimator.RunWithRetry(inputs.Values);
        float[] estimatorOut = outputs[0].AsTensor<float>().ToArray();

        var guided = new float[slice];
        var unguided = new float[slice];
        Array.Copy(estimatorOut, 0, guided, 0, slice);
        Array.Copy(estimatorOut, slice, unguided, 0, slice);
        var combined = new float[slice];
        float cfg = CosyVoiceConstants.CfmCfgRate;
        for (int i = 0; i < slice; i++)
        {
            combined[i] = ((1f + cfg) * guided[i]) - (cfg * unguided[i]);
        }

        return combined;
    }
}
