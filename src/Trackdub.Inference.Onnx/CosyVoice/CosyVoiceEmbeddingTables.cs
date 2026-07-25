using Trackdub.Inference.Onnx.Qwen3Tts.Models;

namespace Trackdub.Inference.Onnx.CosyVoice;

internal sealed class CosyVoiceEmbeddingTables
{
    private CosyVoiceEmbeddingTables(
        float[,] llmSpeechEmbedding,
        float[,] llmLlmEmbedding,
        float[,] llmSpkWeight,
        float[] llmSpkBias,
        float[,] flowInputEmbedding,
        float[,] flowSpkWeight,
        float[] flowSpkBias,
        CosyVoiceLengthRegulator lengthRegulator)
    {
        LlmSpeechEmbedding = llmSpeechEmbedding;
        LlmLlmEmbedding = llmLlmEmbedding;
        LlmSpkWeight = llmSpkWeight;
        LlmSpkBias = llmSpkBias;
        FlowInputEmbedding = flowInputEmbedding;
        FlowSpkWeight = flowSpkWeight;
        FlowSpkBias = flowSpkBias;
        LengthRegulator = lengthRegulator;
    }

    public float[,] LlmSpeechEmbedding { get; }

    public float[,] LlmLlmEmbedding { get; }

    public float[,] LlmSpkWeight { get; }

    public float[] LlmSpkBias { get; }

    public float[,] FlowInputEmbedding { get; }

    public float[,] FlowSpkWeight { get; }

    public float[] FlowSpkBias { get; }

    public CosyVoiceLengthRegulator LengthRegulator { get; }

    public static CosyVoiceEmbeddingTables Load(string modelRootPath)
    {
        string embeddingsDir = Path.Combine(modelRootPath, "embeddings");
        return new CosyVoiceEmbeddingTables(
            NpyReader.ReadFloat2D(Path.Combine(embeddingsDir, "llm_speech_embedding.npy")),
            NpyReader.ReadFloat2D(Path.Combine(embeddingsDir, "llm_llm_embedding.npy")),
            NpyReader.ReadFloat2D(Path.Combine(embeddingsDir, "llm_spk_embed_affine_weight.npy")),
            NpyReader.ReadFloat1D(Path.Combine(embeddingsDir, "llm_spk_embed_affine_bias.npy")),
            NpyReader.ReadFloat2D(Path.Combine(embeddingsDir, "flow_input_embedding.npy")),
            NpyReader.ReadFloat2D(Path.Combine(embeddingsDir, "flow_spk_embed_affine_weight.npy")),
            NpyReader.ReadFloat1D(Path.Combine(embeddingsDir, "flow_spk_embed_affine_bias.npy")),
            CosyVoiceLengthRegulator.Load(Path.Combine(embeddingsDir, "flow_length_regulator.npz")));
    }

    public float[] ProjectLlmSpeaker(float[] campplusEmbedding)
    {
        float[] normalized = Normalize(campplusEmbedding);
        return Affine(normalized, LlmSpkWeight, LlmSpkBias);
    }

    public float[] ProjectFlowSpeaker(float[] campplusEmbedding)
    {
        float[] normalized = Normalize(campplusEmbedding);
        return Affine(normalized, FlowSpkWeight, FlowSpkBias);
    }

    public float[] LookupLlmSpeechEmbedding(int tokenId)
    {
        int width = LlmSpeechEmbedding.GetLength(1);
        var vector = new float[width];
        for (int i = 0; i < width; i++)
        {
            vector[i] = LlmSpeechEmbedding[tokenId, i];
        }

        return vector;
    }

    public float[] LookupFlowTokenEmbedding(int tokenId)
    {
        int width = FlowInputEmbedding.GetLength(1);
        var vector = new float[width];
        for (int i = 0; i < width; i++)
        {
            vector[i] = FlowInputEmbedding[tokenId, i];
        }

        return vector;
    }

    private static float[] Normalize(float[] values)
    {
        double sumSquares = 0d;
        for (int i = 0; i < values.Length; i++)
        {
            sumSquares += values[i] * values[i];
        }

        float norm = (float)Math.Sqrt(sumSquares);
        if (norm <= 1e-12f)
        {
            return values.ToArray();
        }

        var normalized = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            normalized[i] = values[i] / norm;
        }

        return normalized;
    }

    private static float[] Affine(float[] input, float[,] weight, float[] bias)
    {
        int outDim = weight.GetLength(0);
        int inDim = weight.GetLength(1);
        var output = new float[outDim];
        for (int o = 0; o < outDim; o++)
        {
            double sum = bias[o];
            for (int i = 0; i < inDim; i++)
            {
                sum += weight[o, i] * input[i];
            }

            output[o] = (float)sum;
        }

        return output;
    }
}
