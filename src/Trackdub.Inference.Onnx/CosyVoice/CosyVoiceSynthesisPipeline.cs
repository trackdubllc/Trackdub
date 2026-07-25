using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.CosyVoice;

internal sealed class CosyVoiceSynthesisPipeline
{
    private readonly CosyVoiceOnnxSessions sessions;
    private readonly CosyVoiceEmbeddingTables embeddings;
    private readonly CosyVoiceWhisperTokenizer tokenizer;

    public CosyVoiceSynthesisPipeline(
        CosyVoiceOnnxSessions sessions,
        CosyVoiceEmbeddingTables embeddings,
        CosyVoiceWhisperTokenizer tokenizer)
    {
        this.sessions = sessions;
        this.embeddings = embeddings;
        this.tokenizer = tokenizer;
    }

    public float[] Synthesize(
        string targetText,
        string referenceTranscript,
        string referenceClipPath,
        CancellationToken cancellationToken)
    {
        float[] ref22050 = CosyVoiceAudioFeatures.LoadMonoResampled(referenceClipPath, CosyVoiceConstants.SampleRate);
        float[] ref16k = CosyVoiceAudioFeatures.LoadMonoResampled(referenceClipPath, CosyVoiceConstants.CampplusSampleRate);

        float[] campplusInput = CosyVoiceAudioFeatures.ExtractCampplusFbank(ref16k);
        int campplusFrames = campplusInput.Length / CosyVoiceConstants.MelBins;
        float[] speakerEmbedding = RunCampplus(campplusInput, campplusFrames);
        float[] llmSpeaker = embeddings.ProjectLlmSpeaker(speakerEmbedding);
        float[] flowSpeaker = embeddings.ProjectFlowSpeaker(speakerEmbedding);

        (float[] speechTokenizerMel, int speechFrames) = CosyVoiceAudioFeatures.ExtractSpeechTokenizerMel(ref16k);
        long[] promptSpeechTokens = RunSpeechTokenizer(speechTokenizerMel, speechFrames);

        float[,] promptMel = CosyVoiceAudioFeatures.ExtractPromptMel(ref22050);
        int promptMelLength = promptMel.GetLength(0);

        int[] promptTextTokens = tokenizer.Encode(referenceTranscript);
        int[] targetTextTokens = tokenizer.Encode(targetText);
        int[] combinedTextTokens = promptTextTokens.Concat(targetTextTokens).ToArray();
        float[] textEncoderOut = RunTextEncoder(combinedTextTokens);
        int textEncoderLength = combinedTextTokens.Length;

        var lmVectors = new List<float[]>
        {
            CopyRow(embeddings.LlmLlmEmbedding, CosyVoiceConstants.SosToken),
            llmSpeaker,
        };
        for (int i = 0; i < textEncoderLength; i++)
        {
            lmVectors.Add(ExtractTimeSlice(textEncoderOut, textEncoderLength, CosyVoiceConstants.LlmHiddenSize, i));
        }

        lmVectors.Add(CopyRow(embeddings.LlmLlmEmbedding, CosyVoiceConstants.TaskIdToken));
        foreach (long token in promptSpeechTokens)
        {
            lmVectors.Add(embeddings.LookupLlmSpeechEmbedding((int)token));
        }

        int targetTokenCount = targetTextTokens.Length;
        int minLen = (int)(targetTokenCount * CosyVoiceConstants.MinTokenTextRatio);
        int maxLen = (int)(targetTokenCount * CosyVoiceConstants.MaxTokenTextRatio);
        var generatedSpeechTokens = new List<long>();
        var decodedIds = new List<int>();
        for (int step = 0; step < maxLen; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] logits = RunTokenGenerator(lmVectors);
            var logProbs = SoftmaxLogits(logits);
            if (step < minLen)
            {
                logProbs[CosyVoiceConstants.EosToken] = float.NegativeInfinity;
            }

            int sampled = CosyVoiceRasSampler.Sample(logProbs, decodedIds);
            if (sampled == CosyVoiceConstants.EosToken)
            {
                break;
            }

            decodedIds.Add(sampled);
            generatedSpeechTokens.Add(sampled);
            lmVectors.Add(embeddings.LookupLlmSpeechEmbedding(sampled));
        }

        if (generatedSpeechTokens.Count == 0)
        {
            throw new InvalidOperationException("CosyVoice did not generate any speech tokens.");
        }

        long[] allFlowTokens = promptSpeechTokens.Concat(generatedSpeechTokens).ToArray();
        float[] flowTokenEmbeddings = EmbedFlowTokens(allFlowTokens);
        float[] flowEncoderOut = RunFlowEncoder(flowTokenEmbeddings, allFlowTokens.Length);
        float[] promptEncoderOut = ExtractTokenTimeSlice(flowEncoderOut, allFlowTokens.Length, promptSpeechTokens.Length, 0);
        float[] generatedEncoderOut = ExtractTokenTimeSlice(
            flowEncoderOut,
            allFlowTokens.Length,
            generatedSpeechTokens.Count,
            promptSpeechTokens.Length);

        (float[] mu, int totalMelLength) = embeddings.LengthRegulator.Inference(
            promptEncoderOut,
            promptSpeechTokens.Length,
            promptMelLength,
            generatedEncoderOut,
            generatedSpeechTokens.Count);

        var cond = new float[CosyVoiceConstants.MelBins * totalMelLength];
        for (int t = 0; t < promptMelLength; t++)
        {
            for (int c = 0; c < CosyVoiceConstants.MelBins; c++)
            {
                cond[(c * totalMelLength) + t] = promptMel[t, c];
            }
        }

        float[] mel = CosyVoiceFlowMatching.Solve(
            sessions.FlowEstimator.Session,
            mu,
            totalMelLength,
            flowSpeaker,
            cond,
            cancellationToken);

        int outputMelLength = totalMelLength - promptMelLength;
        float[] outputMel = new float[CosyVoiceConstants.MelBins * outputMelLength];
        for (int c = 0; c < CosyVoiceConstants.MelBins; c++)
        {
            for (int t = 0; t < outputMelLength; t++)
            {
                outputMel[(c * outputMelLength) + t] = mel[(c * totalMelLength) + promptMelLength + t];
            }
        }

        return RunHift(outputMel, outputMelLength);
    }

    private float[] RunCampplus(float[] feats, int frames)
    {
        using var inputs = new OnnxInputBatch();
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "input",
            new DenseTensor<float>(feats, [1, frames, CosyVoiceConstants.MelBins])));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            sessions.Campplus.Session.RunWithRetry(inputs.Values);
        return outputs[0].AsTensor<float>().ToArray();
    }

    private long[] RunSpeechTokenizer(float[] feats, int frames)
    {
        using var inputs = new OnnxInputBatch();
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "feats",
            new DenseTensor<float>(feats, [1, CosyVoiceConstants.SpeechTokenizerMelBins, frames])));
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "feats_length",
            new DenseTensor<int>(new[] { frames }, [1])));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            sessions.SpeechTokenizer.Session.RunWithRetry(inputs.Values);
        return outputs[0].AsTensor<long>().ToArray();
    }

    private float[] RunTextEncoder(int[] textTokens)
    {
        long[] tokenIds = textTokens.Select(static t => (long)t).ToArray();
        using var inputs = new OnnxInputBatch();
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "text_tokens",
            new DenseTensor<long>(tokenIds, [1, tokenIds.Length])));
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "text_lengths",
            new DenseTensor<long>(new[] { (long)tokenIds.Length }, [1])));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            sessions.TextEncoder.Session.RunWithRetry(inputs.Values);
        return outputs[0].AsTensor<float>().ToArray();
    }

    private float[] RunTokenGenerator(IReadOnlyList<float[]> lmVectors)
    {
        int seqLen = lmVectors.Count;
        var lmInput = new float[seqLen * CosyVoiceConstants.LlmHiddenSize];
        for (int i = 0; i < seqLen; i++)
        {
            Array.Copy(lmVectors[i], 0, lmInput, i * CosyVoiceConstants.LlmHiddenSize, CosyVoiceConstants.LlmHiddenSize);
        }

        using var inputs = new OnnxInputBatch();
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "lm_input",
            new DenseTensor<float>(lmInput, [1, seqLen, CosyVoiceConstants.LlmHiddenSize])));
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "lm_input_len",
            new DenseTensor<long>(new[] { (long)seqLen }, [1])));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            sessions.TokenGenerator.Session.RunWithRetry(inputs.Values);
        float[] logitsFlat = outputs[0].AsTensor<float>().ToArray();
        int vocab = CosyVoiceConstants.SpeechTokenSize + 1;
        int offset = (seqLen - 1) * vocab;
        var last = new float[vocab];
        Array.Copy(logitsFlat, offset, last, 0, vocab);
        return last;
    }

    private float[] EmbedFlowTokens(long[] tokens)
    {
        int dim = CosyVoiceConstants.FlowTokenEmbedDim;
        var embeddingsFlat = new float[tokens.Length * dim];
        for (int i = 0; i < tokens.Length; i++)
        {
            float[] vector = this.embeddings.LookupFlowTokenEmbedding((int)tokens[i]);
            Array.Copy(vector, 0, embeddingsFlat, i * dim, dim);
        }

        return embeddingsFlat;
    }

    private float[] RunFlowEncoder(float[] tokenEmbeddings, int tokenLength)
    {
        using var inputs = new OnnxInputBatch();
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "token",
            new DenseTensor<float>(tokenEmbeddings, [1, tokenLength, CosyVoiceConstants.FlowTokenEmbedDim])));
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            "token_len",
            new DenseTensor<long>(new[] { (long)tokenLength }, [1])));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            sessions.FlowEncoder.Session.RunWithRetry(inputs.Values);
        return outputs[0].AsTensor<float>().ToArray();
    }

    private float[] RunHift(float[] mel, int melLength)
    {
        using var f0Inputs = new OnnxInputBatch();
        f0Inputs.Add(NamedOnnxValue.CreateFromTensor(
            "mel",
            new DenseTensor<float>(mel, [1, CosyVoiceConstants.MelBins, melLength])));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> f0Outputs =
            sessions.F0Predictor.Session.RunWithRetry(f0Inputs.Values);
        float[] f0 = f0Outputs[0].AsTensor<float>().ToArray();

        int sourceLength = melLength * CosyVoiceConstants.F0UpsampleFactor;
        var f0Upsampled = new float[sourceLength];
        for (int i = 0; i < sourceLength; i++)
        {
            double position = i / (double)Math.Max(1, CosyVoiceConstants.F0UpsampleFactor);
            int left = Math.Min(melLength - 1, (int)Math.Floor(position));
            int right = Math.Min(melLength - 1, left + 1);
            double fraction = position - left;
            f0Upsampled[i] = (float)(f0[left] + ((f0[right] - f0[left]) * fraction));
        }

        using var sourceInputs = new OnnxInputBatch();
        sourceInputs.Add(NamedOnnxValue.CreateFromTensor(
            "f0",
            new DenseTensor<float>(f0Upsampled, [1, sourceLength, 1])));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> sourceOutputs =
            sessions.Source.Session.RunWithRetry(sourceInputs.Values);
        float[] sineSource = sourceOutputs[0].AsTensor<float>().ToArray();

        using var vocoderInputs = new OnnxInputBatch();
        vocoderInputs.Add(NamedOnnxValue.CreateFromTensor(
            "speech_feat",
            new DenseTensor<float>(mel, [1, CosyVoiceConstants.MelBins, melLength])));
        vocoderInputs.Add(NamedOnnxValue.CreateFromTensor(
            "source_signal",
            new DenseTensor<float>(sineSource, [1, 1, sourceLength])));
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> vocoderOutputs =
            sessions.Vocoder.Session.RunWithRetry(vocoderInputs.Values);
        return vocoderOutputs[0].AsTensor<float>().ToArray();
    }

    private static float[] SoftmaxLogits(float[] logits)
    {
        float max = logits.Max();
        var probs = new float[logits.Length];
        double sum = 0d;
        for (int i = 0; i < logits.Length; i++)
        {
            probs[i] = (float)Math.Exp(logits[i] - max);
            sum += probs[i];
        }

        var logProbs = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
        {
            logProbs[i] = (float)Math.Log(probs[i] / sum);
        }

        return logProbs;
    }

    private static float[] CopyRow(float[,] table, int row)
    {
        int width = table.GetLength(1);
        var vector = new float[width];
        for (int i = 0; i < width; i++)
        {
            vector[i] = table[row, i];
        }

        return vector;
    }

    private static float[] ExtractTimeSlice(float[] tensor, int length, int width, int index)
    {
        var slice = new float[width];
        Array.Copy(tensor, index * width, slice, 0, width);
        return slice;
    }

    private static float[] ExtractTokenTimeSlice(float[] tensor, int totalLength, int sliceLength, int startToken)
    {
        int width = CosyVoiceConstants.MelBins;
        var slice = new float[sliceLength * width];
        for (int t = 0; t < sliceLength; t++)
        {
            int srcToken = startToken + t;
            Array.Copy(tensor, srcToken * width, slice, t * width, width);
        }

        return slice;
    }
}
