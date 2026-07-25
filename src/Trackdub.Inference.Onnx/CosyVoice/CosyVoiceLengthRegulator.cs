using System.IO.Compression;

namespace Trackdub.Inference.Onnx.CosyVoice;

internal sealed class CosyVoiceLengthRegulator
{
    private readonly ConvBlock[] blocks;
    private readonly Conv1dWeights finalConv;

    private CosyVoiceLengthRegulator(ConvBlock[] blocks, Conv1dWeights finalConv)
    {
        this.blocks = blocks;
        this.finalConv = finalConv;
    }

    public static CosyVoiceLengthRegulator Load(string npzPath)
    {
        using var archive = ZipFile.OpenRead(npzPath);
        var entries = archive.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), e => e, StringComparer.Ordinal);

        static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        static float[] LoadNpy(byte[] bytes) => LoadNpyArray(bytes).data;

        static (float[] data, int[] shape) LoadNpyArray(byte[] bytes)
        {
            int headerLen = BitConverter.ToUInt16(bytes, 8);
            string header = System.Text.Encoding.ASCII.GetString(bytes, 10, headerLen).Trim();
            int shapeStart = header.IndexOf('(') + 1;
            int shapeEnd = header.IndexOf(')');
            string[] shapeParts = header[shapeStart..shapeEnd].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int[] shape = shapeParts.Select(int.Parse).ToArray();
            int dataOffset = 10 + headerLen;
            int elementCount = shape.Aggregate(1, (a, b) => a * b);
            var data = new float[elementCount];
            Buffer.BlockCopy(bytes, dataOffset, data, 0, elementCount * sizeof(float));
            return (data, shape);
        }

        var blocks = new List<ConvBlock>();
        foreach (int index in new[] { 0, 3, 6, 9 })
        {
            var weightEntry = LoadNpyArray(ReadEntry(entries[$"model.{index}.weight.npy"]));
            blocks.Add(new ConvBlock(
                new Conv1dWeights(weightEntry.data, weightEntry.shape),
                new GroupNormWeights(
                    LoadNpy(ReadEntry(entries[$"model.{index + 1}.weight.npy"])),
                    LoadNpy(ReadEntry(entries[$"model.{index + 1}.bias.npy"])))));
        }

        var final = LoadNpyArray(ReadEntry(entries["model.12.weight.npy"]));
        return new CosyVoiceLengthRegulator(blocks.ToArray(), new Conv1dWeights(final.data, final.shape));
    }

    public (float[] encoded, int totalMelLength) Inference(
        float[] promptEncoderOut,
        int promptTokenLength,
        int promptMelLength,
        float[] generatedEncoderOut,
        int generatedTokenLength)
    {
        int melLen2 = (int)(generatedTokenLength / (double)CosyVoiceConstants.InputFrameRate * CosyVoiceConstants.SampleRate / CosyVoiceConstants.MelHop);
        int channels = CosyVoiceConstants.MelBins;

        float[] x2 = InterpolateTokens(generatedEncoderOut, generatedTokenLength, channels, melLen2);
        float[] x;
        int melLen1 = promptMelLength;
        if (promptTokenLength > 0)
        {
            float[] x1 = InterpolateTokens(promptEncoderOut, promptTokenLength, channels, melLen1);
            x = new float[(melLen1 + melLen2) * channels];
            Array.Copy(x1, 0, x, 0, x1.Length);
            Array.Copy(x2, 0, x, x1.Length, x2.Length);
        }
        else
        {
            x = x2;
            melLen1 = 0;
        }

        int totalLength = melLen1 + melLen2;
        float[] working = ApplyModel(x, channels, totalLength);
        return (working, totalLength);
    }

    private float[] InterpolateTokens(float[] source, int tokenLength, int channels, int targetLength)
    {
        if (tokenLength <= 0)
        {
            return [];
        }

        if (tokenLength > 40)
        {
            int headTokens = 20;
            int tailTokens = 20;
            int headMel = (int)(headTokens / (double)CosyVoiceConstants.InputFrameRate * CosyVoiceConstants.SampleRate / CosyVoiceConstants.MelHop);
            int tailMel = headMel;
            int midMel = targetLength - (headMel * 2);
            float[] head = LinearInterp(source, tokenLength, channels, 0, headTokens, headMel);
            float[] mid = LinearInterp(source, tokenLength, channels, headTokens, tokenLength - tailTokens, midMel);
            float[] tail = LinearInterp(source, tokenLength, channels, tokenLength - tailTokens, tokenLength, tailMel);
            float[] combined = new float[(headMel + midMel + tailMel) * channels];
            Array.Copy(head, 0, combined, 0, head.Length);
            Array.Copy(mid, 0, combined, head.Length, mid.Length);
            Array.Copy(tail, 0, combined, head.Length + mid.Length, tail.Length);
            return combined;
        }

        return LinearInterp(source, tokenLength, channels, 0, tokenLength, targetLength);
    }

    /// <summary>
    /// Linear interpolation between encoder tokens into a target mel length.
    /// Internal for unit tests of large-dimension index math.
    /// </summary>
    internal static float[] LinearInterp(float[] source, int tokenLength, int channels, int startToken, int endToken, int targetLength)
    {
        int sourceTokens = endToken - startToken;
        var output = new float[targetLength * channels];
        if (sourceTokens <= 1)
        {
            int baseIndex = startToken * channels;
            for (int t = 0; t < targetLength; t++)
            {
                Array.Copy(source, baseIndex, output, t * channels, channels);
            }

            return output;
        }

        for (int t = 0; t < targetLength; t++)
        {
            // Cast before multiply so large token/target dims cannot overflow int before double conversion.
            double position = t * (double)(sourceTokens - 1) / Math.Max(1, targetLength - 1);
            int left = (int)Math.Floor(position);
            int right = Math.Min(sourceTokens - 1, left + 1);
            double fraction = position - left;
            int leftIndex = ((startToken + left) * channels);
            int rightIndex = ((startToken + right) * channels);
            int dst = t * channels;
            for (int c = 0; c < channels; c++)
            {
                output[dst + c] = (float)(source[leftIndex + c] + ((source[rightIndex + c] - source[leftIndex + c]) * fraction));
            }
        }

        return output;
    }

    private float[] ApplyModel(float[] input, int channels, int length)
    {
        float[] current = input;
        foreach (ConvBlock block in blocks)
        {
            current = block.Convolve(current, channels, length);
            current = block.Normalize(current, channels, length);
            current = Mish(current);
        }

        return finalConv.Convolve(current, channels, length);
    }

    private static float[] Mish(float[] values)
    {
        var output = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            float x = values[i];
            output[i] = x * (float)Math.Tanh(Math.Log(1d + Math.Exp(x)));
        }

        return output;
    }

    private sealed record ConvBlock(Conv1dWeights Conv, GroupNormWeights Norm)
    {
        public float[] Convolve(float[] input, int channels, int length) => Conv.Convolve(input, channels, length);

        public float[] Normalize(float[] input, int channels, int length) => Norm.Apply(input, channels, length);
    }

    private sealed class Conv1dWeights
    {
        private readonly float[] weights;
        private readonly int outChannels;
        private readonly int inChannels;
        private readonly int kernel;

        public Conv1dWeights(float[] weights, int[] shape)
        {
            this.weights = weights;
            outChannels = shape[0];
            inChannels = shape[1];
            kernel = shape[2];
        }

        public float[] Convolve(float[] input, int channels, int length)
        {
            int pad = kernel / 2;
            var output = new float[channels * length];
            for (int t = 0; t < length; t++)
            {
                for (int oc = 0; oc < outChannels; oc++)
                {
                    double sum = 0d;
                    for (int ic = 0; ic < inChannels; ic++)
                    {
                        for (int k = 0; k < kernel; k++)
                        {
                            int sampleIndex = t + k - pad;
                            float sample = sampleIndex >= 0 && sampleIndex < length
                                ? input[(sampleIndex * channels) + ic]
                                : 0f;
                            int weightIndex = (((oc * inChannels) + ic) * kernel) + k;
                            sum += weights[weightIndex] * sample;
                        }
                    }

                    output[(t * channels) + oc] = (float)sum;
                }
            }

            return output;
        }
    }

    private sealed class GroupNormWeights
    {
        private readonly float[] weight;
        private readonly float[] bias;

        public GroupNormWeights(float[] weight, float[] bias)
        {
            this.weight = weight;
            this.bias = bias;
        }

        public float[] Apply(float[] input, int channels, int length)
        {
            var output = new float[input.Length];
            for (int c = 0; c < channels; c++)
            {
                double mean = 0d;
                double var = 0d;
                for (int t = 0; t < length; t++)
                {
                    mean += input[(t * channels) + c];
                }

                mean /= length;
                for (int t = 0; t < length; t++)
                {
                    double delta = input[(t * channels) + c] - mean;
                    var += delta * delta;
                }

                double std = Math.Sqrt((var / length) + 1e-5);
                for (int t = 0; t < length; t++)
                {
                    int index = (t * channels) + c;
                    output[index] = (float)(((input[index] - mean) / std * weight[c]) + bias[c]);
                }
            }

            return output;
        }
    }
}
