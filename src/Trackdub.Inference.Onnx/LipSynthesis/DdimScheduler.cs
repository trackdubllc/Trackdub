namespace Trackdub.Inference.Onnx.LipSynthesis;

/// <summary>
/// Pure-C# DDIM (Denoising Diffusion Implicit Models) scheduler for LatentSync.
/// Parameters match the LatentSync 1.6 diffusion config: scaled_linear beta schedule,
/// 1000 training timesteps, default 25 inference steps, eta=0 (deterministic).
/// </summary>
internal sealed class DdimScheduler
{
    private const int NumTrainTimesteps = 1000;
    private const float BetaStart = 0.00085f;
    private const float BetaEnd = 0.012f;

    private readonly float[] _alphasCumprod;
    private readonly int _numInferenceSteps;
    private readonly int[] _timesteps;

    public int[] Timesteps => _timesteps;

    public DdimScheduler(int numInferenceSteps = 25)
    {
        _numInferenceSteps = numInferenceSteps;
        _alphasCumprod = ComputeAlphasCumprod();
        _timesteps = ComputeTimesteps();
    }

    /// <summary>
    /// Performs one DDIM reverse-diffusion step (eta=0, fully deterministic).
    /// </summary>
    /// <param name="modelOutput">Noise predicted by the U-Net at this timestep.</param>
    /// <param name="timestep">Current timestep index into the training schedule.</param>
    /// <param name="sample">Current noisy latent x_t.</param>
    /// <returns>Denoised latent x_{t-1}.</returns>
    public float[] Step(ReadOnlySpan<float> modelOutput, int timestep, ReadOnlySpan<float> sample)
    {
        int prevTimestep = timestep - NumTrainTimesteps / _numInferenceSteps;

        float alphasProdT = _alphasCumprod[timestep];
        float alphasProdTPrev = prevTimestep >= 0 ? _alphasCumprod[prevTimestep] : 1f;
        float betaProdT = 1f - alphasProdT;
        float betaProdTPrev = 1f - alphasProdTPrev;

        float sqrtAlphaProdT = MathF.Sqrt(alphasProdT);
        float sqrtAlphaProdTPrev = MathF.Sqrt(alphasProdTPrev);
        float sqrtBetaProdTPrev = MathF.Sqrt(betaProdTPrev);
        float sqrtBetaProdT = MathF.Sqrt(betaProdT);

        var prevSample = new float[sample.Length];
        for (int i = 0; i < sample.Length; i++)
        {
            // predicted x_0 (denoised original)
            float predOrigSample = (sample[i] - sqrtBetaProdT * modelOutput[i]) / sqrtAlphaProdT;
            // direction pointing to x_t
            float dirToXt = sqrtBetaProdTPrev * modelOutput[i];
            prevSample[i] = sqrtAlphaProdTPrev * predOrigSample + dirToXt;
        }

        return prevSample;
    }

    /// <summary>
    /// Encodes a clean sample x_0 into a noisy latent x_t at the given timestep
    /// (forward diffusion, used to set the starting noisy latent from a reference frame).
    /// </summary>
    public float[] AddNoise(ReadOnlySpan<float> original, ReadOnlySpan<float> noise, int timestep)
    {
        float sqrtAlphaProd = MathF.Sqrt(_alphasCumprod[timestep]);
        float sqrtOneMinusAlphaProd = MathF.Sqrt(1f - _alphasCumprod[timestep]);

        var noisy = new float[original.Length];
        for (int i = 0; i < original.Length; i++)
        {
            noisy[i] = sqrtAlphaProd * original[i] + sqrtOneMinusAlphaProd * noise[i];
        }

        return noisy;
    }

    private static float[] ComputeAlphasCumprod()
    {
        float sqrtBetaStart = MathF.Sqrt(BetaStart);
        float sqrtBetaEnd = MathF.Sqrt(BetaEnd);

        float[] betas = new float[NumTrainTimesteps];
        for (int i = 0; i < NumTrainTimesteps; i++)
        {
            float t = (float)i / (NumTrainTimesteps - 1);
            float sqrtBeta = sqrtBetaStart + t * (sqrtBetaEnd - sqrtBetaStart);
            betas[i] = sqrtBeta * sqrtBeta;
        }

        float[] alphasCumprod = new float[NumTrainTimesteps];
        float cumProd = 1f;
        for (int i = 0; i < NumTrainTimesteps; i++)
        {
            cumProd *= 1f - betas[i];
            alphasCumprod[i] = cumProd;
        }

        return alphasCumprod;
    }

    private int[] ComputeTimesteps()
    {
        int stepRatio = NumTrainTimesteps / _numInferenceSteps;
        // Descending order: [999, 959, ..., 39] for 25 steps
        var timesteps = new int[_numInferenceSteps];
        for (int i = 0; i < _numInferenceSteps; i++)
        {
            timesteps[i] = (_numInferenceSteps - 1 - i) * stepRatio + stepRatio - 1;
        }

        return timesteps;
    }
}
