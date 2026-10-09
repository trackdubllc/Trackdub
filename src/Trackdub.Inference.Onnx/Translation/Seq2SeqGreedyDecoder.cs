using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Translation;

/// <summary>
/// Greedy decoding for Optimum-exported encoder-decoder translation models.
/// </summary>
/// <remarks>
/// A merged decoder (<c>use_cache_branch</c> input plus <c>present.*</c> outputs) is decoded with its
/// key/value cache: step 0 runs the no-cache branch over the start token and yields the self-attention
/// and cross-attention caches; every later step feeds only the newest token. Decoders without a cache
/// re-run the whole generated prefix every step, which is quadratic in the output length.
/// </remarks>
internal static class Seq2SeqGreedyDecoder
{
    private const string UseCacheBranchInput = "use_cache_branch";
    private const string PastPrefix = "past_key_values.";
    private const string PresentPrefix = "present.";

    internal static bool SupportsKeyValueCache(InferenceSession decoderSession) =>
        decoderSession.InputMetadata.ContainsKey(UseCacheBranchInput) &&
        decoderSession.InputMetadata.Keys.Any(static name => name.StartsWith(PastPrefix, StringComparison.Ordinal)) &&
        decoderSession.OutputMetadata.Keys.Any(static name => name.StartsWith(PresentPrefix, StringComparison.Ordinal));

    internal static List<long> Decode(
        InferenceSession decoderSession,
        Tensor<float> encoderHiddenStates,
        long[] encoderAttentionMask,
        long decoderStartTokenId,
        long endOfSentenceTokenId,
        int padTokenId,
        int maxSteps,
        ExecutionProviderKind? provider,
        CancellationToken cancellationToken) =>
        SupportsKeyValueCache(decoderSession)
            ? DecodeWithCache(decoderSession, encoderHiddenStates, encoderAttentionMask, decoderStartTokenId,
                endOfSentenceTokenId, padTokenId, maxSteps, provider, cancellationToken)
            : DecodeWithoutCache(decoderSession, encoderHiddenStates, encoderAttentionMask, decoderStartTokenId,
                endOfSentenceTokenId, padTokenId, maxSteps, provider, cancellationToken);

    private static List<long> DecodeWithCache(
        InferenceSession decoderSession,
        Tensor<float> encoderHiddenStates,
        long[] encoderAttentionMask,
        long decoderStartTokenId,
        long endOfSentenceTokenId,
        int padTokenId,
        int maxSteps,
        ExecutionProviderKind? provider,
        CancellationToken cancellationToken)
    {
        var generated = new List<long>();
        var maskTensor = new DenseTensor<long>(encoderAttentionMask, [1, encoderAttentionMask.Length]);

        // Outputs of the previous step stay alive until the next run has consumed them.
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? previous = null;
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? firstStep = null;
        try
        {
            long nextInputToken = decoderStartTokenId;
            for (int step = 0; step < maxSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool useCache = step > 0;
                var inputs = new List<NamedOnnxValue>(decoderSession.InputMetadata.Count);
                foreach ((string name, NodeMetadata metadata) in decoderSession.InputMetadata)
                {
                    inputs.Add(name switch
                    {
                        "input_ids" => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { nextInputToken }, [1, 1])),
                        "encoder_hidden_states" => NamedOnnxValue.CreateFromTensor(name, encoderHiddenStates),
                        "encoder_attention_mask" or "attention_mask" => NamedOnnxValue.CreateFromTensor(name, maskTensor),
                        UseCacheBranchInput => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<bool>(new[] { useCache }, [1])),
                        _ when name.StartsWith(PastPrefix, StringComparison.Ordinal) =>
                            NamedOnnxValue.CreateFromTensor(name, useCache
                                ? ResolvePast(name, previous!, firstStep!)
                                : CreatePlaceholderPast(metadata)),
                        _ => throw new NotSupportedException($"Decoder input '{name}' is not supported.")
                    });
                }

                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                    decoderSession.RunWithRetry(inputs, cancellationToken: cancellationToken, provider: provider);
                if (step == 0)
                {
                    firstStep = outputs;
                }
                else if (!ReferenceEquals(previous, firstStep))
                {
                    previous?.Dispose();
                }

                previous = outputs;
                int nextToken = ArgMaxLastPosition(
                    outputs.Single(static value => value.Name == "logits").AsTensor<float>(),
                    padTokenId);
                if (nextToken < 0 || nextToken == endOfSentenceTokenId)
                {
                    break;
                }

                generated.Add(nextToken);
                nextInputToken = nextToken;
            }
        }
        finally
        {
            if (!ReferenceEquals(previous, firstStep))
            {
                previous?.Dispose();
            }

            firstStep?.Dispose();
        }

        return generated;
    }

    // Self-attention cache comes from the previous step; the cross-attention cache is computed once,
    // by the no-cache branch at step 0, and reused for every later step.
    private static Tensor<float> ResolvePast(
        string pastName,
        IReadOnlyCollection<DisposableNamedOnnxValue> previous,
        IReadOnlyCollection<DisposableNamedOnnxValue> firstStep)
    {
        string presentName = string.Concat(PresentPrefix, pastName.AsSpan(PastPrefix.Length));
        IReadOnlyCollection<DisposableNamedOnnxValue> source =
            pastName.Contains(".encoder.", StringComparison.Ordinal) ? firstStep : previous;
        return source.Single(value => value.Name == presentName).AsTensor<float>();
    }

    // The no-cache branch ignores past inputs, but the graph still needs well-formed tensors.
    // One zero position avoids zero-sized tensors, which some GPU providers reject.
    private static DenseTensor<float> CreatePlaceholderPast(NodeMetadata metadata)
    {
        int[] dims = metadata.Dimensions.Select(static d => d > 0 ? d : 1).ToArray();
        dims[0] = 1;
        return new DenseTensor<float>(dims);
    }

    private static List<long> DecodeWithoutCache(
        InferenceSession decoderSession,
        Tensor<float> encoderHiddenStates,
        long[] encoderAttentionMask,
        long decoderStartTokenId,
        long endOfSentenceTokenId,
        int padTokenId,
        int maxSteps,
        ExecutionProviderKind? provider,
        CancellationToken cancellationToken)
    {
        var tokens = new List<long> { decoderStartTokenId };
        var maskTensor = new DenseTensor<long>(encoderAttentionMask, [1, encoderAttentionMask.Length]);
        for (int step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = new List<NamedOnnxValue>(decoderSession.InputMetadata.Count);
            DenseTensor<long> idsTensor = new(tokens.ToArray(), [1, tokens.Count]);
            foreach ((string name, NodeMetadata metadata) in decoderSession.InputMetadata)
            {
                inputs.Add(name switch
                {
                    "input_ids" => NamedOnnxValue.CreateFromTensor(name, idsTensor),
                    "encoder_hidden_states" => NamedOnnxValue.CreateFromTensor(name, encoderHiddenStates),
                    "encoder_attention_mask" or "attention_mask" => NamedOnnxValue.CreateFromTensor(name, maskTensor),
                    UseCacheBranchInput => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<bool>(new[] { false }, [1])),
                    _ when name.StartsWith(PastPrefix, StringComparison.Ordinal) =>
                        NamedOnnxValue.CreateFromTensor(name, CreateEmptyPast(metadata)),
                    _ => throw new NotSupportedException($"Decoder input '{name}' is not supported.")
                });
            }

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                decoderSession.RunWithRetry(inputs, cancellationToken: cancellationToken, provider: provider);
            int nextToken = ArgMaxLastPosition(
                outputs.Single(static value => value.Name == "logits").AsTensor<float>(),
                padTokenId);
            if (nextToken < 0 || nextToken == endOfSentenceTokenId)
            {
                break;
            }

            tokens.Add(nextToken);
        }

        return tokens.Skip(1).ToList();
    }

    private static DenseTensor<float> CreateEmptyPast(NodeMetadata metadata)
    {
        int[] dims = metadata.Dimensions.Select(static d => d > 0 ? d : 1).ToArray();
        dims[0] = 1;
        if (dims.Length > 2)
        {
            dims[2] = 0;
        }

        return new DenseTensor<float>(Array.Empty<float>(), dims);
    }

    internal static int ArgMaxLastPosition(Tensor<float> logits, int padTokenId)
    {
        int sequenceLength = logits.Dimensions[1];
        int vocabularySize = logits.Dimensions[2];
        int bestToken = -1;
        float bestValue = float.NegativeInfinity;
        if (logits is DenseTensor<float> dense)
        {
            ReadOnlySpan<float> row = dense.Buffer.Span.Slice((sequenceLength - 1) * vocabularySize, vocabularySize);
            for (int token = 0; token < row.Length; token++)
            {
                if (token != padTokenId && row[token] > bestValue)
                {
                    bestValue = row[token];
                    bestToken = token;
                }
            }

            return bestToken;
        }

        for (int token = 0; token < vocabularySize; token++)
        {
            float value = logits[0, sequenceLength - 1, token];
            if (token != padTokenId && value > bestValue)
            {
                bestValue = value;
                bestToken = token;
            }
        }

        return bestToken;
    }
}
