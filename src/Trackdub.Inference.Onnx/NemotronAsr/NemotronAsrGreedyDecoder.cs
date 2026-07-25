using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.NemotronAsr;

internal sealed class NemotronAsrGreedyDecoder(
    OnnxExecutionSessionFactory.NemotronAsrSessionLease sessionLease,
    NemotronAsrSentencePieceVocab vocab)
{
    private const int MaxSymbolsPerStep = 10;
    // NeMo greedy decode seeds the predictor with BOS (0), not the RNNT blank id.
    private const int DecoderStartTokenId = 0;

    private readonly NemotronAsrModelConfig config = NemotronAsrModelConfig.FromSessions(
        sessionLease.EncoderSession,
        sessionLease.DecoderJointSession,
        vocab.Count);

    public string? DetectedLanguage { get; private set; }

    public IReadOnlyList<int> Decode(float[,] mel, long promptIndex)
    {
        ArgumentNullException.ThrowIfNull(mel);
        ResetState(out DenseTensor<float> cacheLastChannel, out DenseTensor<float> cacheLastTime,
            out DenseTensor<long> cacheLastChannelLength, out DenseTensor<float> state1,
            out DenseTensor<float> state2, out int lastToken);

        int totalFrames = mel.GetLength(1);
        var allTokens = new List<int>();
        var featureExtractor = new NemotronAsrMelFeatureExtractor();
        int chunkIndex = 0;

        for (int frameOffset = 0; frameOffset < totalFrames; frameOffset += NemotronAsrMelFeatureExtractor.ChunkFrames)
        {
            int mainFrameCount = Math.Min(NemotronAsrMelFeatureExtractor.ChunkFrames, totalFrames - frameOffset);
            float[] chunkData = featureExtractor.BuildChunk(mel, frameOffset, mainFrameCount, includePreEncodeCache: chunkIndex > 0);
            using NemotronAsrInputSet encoderInputs = BuildEncoderInputs(
                chunkData,
                NemotronAsrMelFeatureExtractor.PreEncodeCacheFrames + mainFrameCount,
                cacheLastChannel,
                cacheLastTime,
                cacheLastChannelLength,
                promptIndex);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderResults =
                sessionLease.EncoderSession.RunWithRetry(encoderInputs.Values);

            Tensor<float> encoded = GetTensor<float>(encoderResults, "encoded");
            int encodedLength = (int)GetTensor<long>(encoderResults, "encoded_len").FirstOrDefault();
            cacheLastChannel = CloneTensor<float>(GetTensor<float>(encoderResults, "cache_last_channel_next"));
            cacheLastTime = CloneTensor<float>(GetTensor<float>(encoderResults, "cache_last_time_next"));
            cacheLastChannelLength = CloneTensor<long>(GetTensor<long>(encoderResults, "cache_last_channel_len_next"));

            DecodeEncoderFrames(encoded, encodedLength, allTokens, ref state1, ref state2, ref lastToken);
            chunkIndex++;
        }

        return allTokens;
    }

    public string DecodeText(IReadOnlyList<int> tokens)
    {
        var visibleTokens = new List<int>(tokens.Count);
        foreach (int token in tokens)
        {
            if (token < 0 || token >= vocab.Count)
            {
                continue;
            }

            if (vocab.LanguageTagIds.Contains(token))
            {
                DetectedLanguage ??= NemotronAsrLanguagePrompts.TryExtractLanguageTag(vocab.DecodeSingle(token));
                continue;
            }

            visibleTokens.Add(token);
        }

        return vocab.Decode(visibleTokens).Trim();
    }

    private void DecodeEncoderFrames(
        Tensor<float> encoded,
        int encodedLength,
        List<int> tokens,
        ref DenseTensor<float> state1,
        ref DenseTensor<float> state2,
        ref int lastToken)
    {
        NemotronAsrEncodedTensorLayout.EncodedLayout layout = NemotronAsrEncodedTensorLayout.Resolve(
            encoded.Dimensions,
            encodedLength,
            config.HiddenDim);
        for (int frameIndex = 0; frameIndex < layout.AvailableFrames; frameIndex++)
        {
            DenseTensor<float> frame = NemotronAsrEncodedTensorLayout.SliceFrame(encoded, layout, frameIndex);
            for (int symbol = 0; symbol < MaxSymbolsPerStep; symbol++)
            {
                using NemotronAsrInputSet decoderInputs = BuildDecoderInputs(frame, lastToken, state1, state2);
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoderResults =
                    sessionLease.DecoderJointSession.RunWithRetry(decoderInputs.Values);

                Tensor<float> logits = GetTensor<float>(decoderResults, "outputs");
                int nextToken = ArgMax(logits);
                // Update LSTM state unconditionally — carry state through blank frames
                // so subsequent frames decode from the correct predictor state.
                state1 = CloneTensor<float>(GetTensor<float>(decoderResults, "output_states_1"));
                state2 = CloneTensor<float>(GetTensor<float>(decoderResults, "output_states_2"));
                if (nextToken == config.BlankId)
                {
                    break;
                }

                lastToken = nextToken;
                tokens.Add(nextToken);
            }
        }
    }

    private NemotronAsrInputSet BuildEncoderInputs(
        float[] chunkData,
        int chunkLength,
        DenseTensor<float> cacheLastChannel,
        DenseTensor<float> cacheLastTime,
        DenseTensor<long> cacheLastChannelLength,
        long promptIndex)
    {
        var values = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                "processed_signal",
                new DenseTensor<float>(
                    chunkData,
                    [1, NemotronAsrMelFeatureExtractor.MelBins, NemotronAsrMelFeatureExtractor.ChunkInputFrames])),
            NamedOnnxValue.CreateFromTensor(
                "processed_signal_length",
                new DenseTensor<long>(new[] { (long)chunkLength }, [1])),
            NamedOnnxValue.CreateFromTensor("cache_last_channel", cacheLastChannel),
            NamedOnnxValue.CreateFromTensor("cache_last_time", cacheLastTime),
            NamedOnnxValue.CreateFromTensor("cache_last_channel_len", cacheLastChannelLength)
        };

        if (config.HasPromptInput)
        {
            values.Add(NamedOnnxValue.CreateFromTensor(
                "prompt_index",
                new DenseTensor<long>(new[] { promptIndex }, [1])));
        }

        return new NemotronAsrInputSet(values);
    }

    private NemotronAsrInputSet BuildDecoderInputs(
        DenseTensor<float> frame,
        int lastToken,
        DenseTensor<float> state1,
        DenseTensor<float> state2) =>
        new(
        [
            NamedOnnxValue.CreateFromTensor("encoder_outputs", frame),
            CreateDecoderTokenInput("targets", lastToken, [1, 1]),
            CreateDecoderTokenInput("target_length", 1, [1]),
            NamedOnnxValue.CreateFromTensor("input_states_1", state1),
            NamedOnnxValue.CreateFromTensor("input_states_2", state2)
        ]);

    private NamedOnnxValue CreateDecoderTokenInput(string inputName, long value, int[] dimensions)
    {
        if (!sessionLease.DecoderJointSession.InputMetadata.TryGetValue(inputName, out NodeMetadata? metadata))
        {
            throw new InvalidOperationException($"Nemotron decoder input '{inputName}' was not found.");
        }

        return metadata.ElementDataType switch
        {
            TensorElementType.Int32 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<int>(new[] { checked((int)value) }, dimensions)),
            TensorElementType.Int64 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<long>(new[] { value }, dimensions)),
            _ => throw new NotSupportedException(
                $"Nemotron decoder input '{inputName}' uses unsupported token tensor element type '{metadata.ElementDataType}'.")
        };
    }

    private void ResetState(
        out DenseTensor<float> cacheLastChannel,
        out DenseTensor<float> cacheLastTime,
        out DenseTensor<long> cacheLastChannelLength,
        out DenseTensor<float> state1,
        out DenseTensor<float> state2,
        out int lastToken)
    {
        // Allocate cache tensors using the exact shapes declared in the encoder session's
        // input metadata (instead of assuming a [layers, 1, ...] layout from config).
        // This matches the bundled export layout (batch-first or whatever the model reports
        // for cache_last_channel / cache_last_time) and prevents dimension-swapped inputs
        // to ONNX Runtime. See P1 review thread on PR #335.
        var encMeta = sessionLease.EncoderSession.InputMetadata;

        int[] chShape = ResolveCacheShape(encMeta, "cache_last_channel",
            [config.NumEncoderLayers, 1, config.LeftContext, config.HiddenDim]);
        int chCount = chShape.Aggregate(1, static (prod, d) => checked(prod * d));
        cacheLastChannel = new DenseTensor<float>(new float[chCount], chShape);

        int[] tmShape = ResolveCacheShape(encMeta, "cache_last_time",
            [config.NumEncoderLayers, 1, config.HiddenDim, config.ConvContext]);
        int tmCount = tmShape.Aggregate(1, static (prod, d) => checked(prod * d));
        cacheLastTime = new DenseTensor<float>(new float[tmCount], tmShape);

        cacheLastChannelLength = new DenseTensor<long>(new long[] { 0 }, [1]);
        state1 = new DenseTensor<float>(
            new float[checked(config.DecoderLstmLayers * config.DecoderLstmDim)],
            [config.DecoderLstmLayers, 1, config.DecoderLstmDim]);
        state2 = new DenseTensor<float>(
            new float[checked(config.DecoderLstmLayers * config.DecoderLstmDim)],
            [config.DecoderLstmLayers, 1, config.DecoderLstmDim]);
        lastToken = DecoderStartTokenId;
        DetectedLanguage = null;
    }

    private static int[] ResolveCacheShape(
        IReadOnlyDictionary<string, NodeMetadata> meta,
        string name,
        int[] fallback)
    {
        if (meta.TryGetValue(name, out NodeMetadata? m) && m.Dimensions.Length == fallback.Length)
        {
            var dims = new int[fallback.Length];
            for (int i = 0; i < dims.Length; i++)
            {
                dims[i] = m.Dimensions[i] > 0 ? m.Dimensions[i] : fallback[i];
            }
            return dims;
        }
        return (int[])fallback.Clone();
    }

    private static Tensor<T> GetTensor<T>(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        string name)
        where T : unmanaged =>
        results.Single(result => string.Equals(result.Name, name, StringComparison.Ordinal)).AsTensor<T>();

    private static DenseTensor<T> CloneTensor<T>(Tensor<T> tensor)
        where T : unmanaged
    {
        int[] dimensions = tensor.Dimensions.ToArray();
        var data = new T[tensor.Length];
        int index = 0;
        foreach (T value in tensor)
        {
            data[index++] = value;
        }

        return new DenseTensor<T>(data, dimensions);
    }

    private static int ArgMax(Tensor<float> logits)
    {
        int bestIndex = 0;
        float bestValue = float.NegativeInfinity;
        int index = 0;
        foreach (float value in logits)
        {
            if (value > bestValue)
            {
                bestValue = value;
                bestIndex = index;
            }

            index++;
        }

        return bestIndex;
    }

    private sealed record NemotronAsrModelConfig(
        int NumEncoderLayers,
        int HiddenDim,
        int LeftContext,
        int ConvContext,
        int DecoderLstmDim,
        int DecoderLstmLayers,
        int BlankId,
        bool HasPromptInput)
    {
        public static NemotronAsrModelConfig FromSessions(
            InferenceSession encoderSession,
            InferenceSession decoderJointSession,
            int vocabSize)
        {
            int numEncoderLayers = 24;
            int hiddenDim = 1024;
            int leftContext = 56;
            int convContext = 8;
            bool hasPromptInput = encoderSession.InputMetadata.ContainsKey("prompt_index");

            if (encoderSession.InputMetadata.TryGetValue("cache_last_channel", out NodeMetadata? channelMetadata) &&
                channelMetadata.Dimensions.Length >= 4)
            {
                numEncoderLayers = PositiveOrDefault(channelMetadata.Dimensions[0], numEncoderLayers);
                leftContext = PositiveOrDefault(channelMetadata.Dimensions[2], leftContext);
                hiddenDim = PositiveOrDefault(channelMetadata.Dimensions[3], hiddenDim);
            }

            if (encoderSession.InputMetadata.TryGetValue("cache_last_time", out NodeMetadata? timeMetadata) &&
                timeMetadata.Dimensions.Length >= 4)
            {
                convContext = PositiveOrDefault(timeMetadata.Dimensions[3], convContext);
            }

            int decoderLayers = 2;
            int decoderDim = 640;
            if (decoderJointSession.InputMetadata.TryGetValue("input_states_1", out NodeMetadata? stateMetadata) &&
                stateMetadata.Dimensions.Length >= 3)
            {
                decoderLayers = PositiveOrDefault(stateMetadata.Dimensions[0], decoderLayers);
                decoderDim = PositiveOrDefault(stateMetadata.Dimensions[2], decoderDim);
            }

            return new NemotronAsrModelConfig(
                numEncoderLayers,
                hiddenDim,
                leftContext,
                convContext,
                decoderDim,
                decoderLayers,
                checked(vocabSize - 1),
                hasPromptInput);
        }

        private static int PositiveOrDefault(int value, int fallback) =>
            value > 0 ? value : fallback;
    }
}
