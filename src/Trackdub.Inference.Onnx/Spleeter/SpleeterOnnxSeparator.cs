using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Spleeter;

internal sealed class SpleeterOnnxSeparator : ISpleeterSeparator
{
    private readonly SpleeterStftProcessor stftProcessor = new();

    public async Task<SpleeterSeparation> SeparateAsync(
        SpleeterSeparatorRequest request,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        int originalLength = request.Left.Length;
        int targetFrames = stftProcessor.GetTargetFrameCount(originalLength);
        int numSplits = targetFrames / SpleeterModelConstants.TimePad;

        ExecutionProviderKind provider = request.Plan.ExecutionProvider ?? ExecutionProviderKind.Cpu;
        bool allowTrtInitFallback = !request.Plan.RequirePreferredExecutionProvider;

        string vocalsModelPath = SpleeterModelConstants.ResolveModelPath(
            request.ModelRootPath,
            SpleeterModelConstants.VocalsModelFileName);
        string accModelPath = SpleeterModelConstants.ResolveModelPath(
            request.ModelRootPath,
            SpleeterModelConstants.AccompanimentModelFileName);

        string? selectedProvider = null;
        string? bootstrapDetail = null;

        float[] vocalsMono = new float[originalLength];
        float[] accMono = new float[originalLength];
        float[] windowSum = new float[originalLength];
        int totalSteps = numSplits * 2;
        progress?.Report(new StemSeparationProgress(0, totalSteps, 0d, 1d));

        // One 512-frame split at a time: STFT block, vocals inference, accompaniment
        // inference, overlap-add — no audio-length spectrogram or mask intermediates, and
        // no model session is held while reconstructing.
        for (int split = 0; split < numSplits; split++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using SpleeterStftBlock block = stftProcessor.ForwardBlock(
                request.Left, request.Right, split * SpleeterModelConstants.TimePad,
                SpleeterModelConstants.TimePad);
            var inputTensor = new DenseTensor<float>(
                block.Magnitudes,
                [2, 1, SpleeterModelConstants.TimePad, SpleeterModelConstants.MaxFreqBins]);

            StemMaskResult vocalsResult = await RunStemMaskAsync(
                "spleeter-vocals",
                vocalsModelPath,
                provider,
                allowTrtInitFallback,
                inputTensor,
                cancellationToken).ConfigureAwait(false);
            if (selectedProvider is null)
            {
                selectedProvider = vocalsResult.SelectedProvider;
                bootstrapDetail = vocalsResult.BootstrapDetail;
            }
            else
            {
                EnsureConsistentProvider(selectedProvider, vocalsResult.SelectedProvider);
            }

            progress?.Report(new StemSeparationProgress(
                (split * 2) + 1, totalSteps,
                (split + 0.5d) / numSplits, (split + 1d) / numSplits));

            StemMaskResult accResult = await RunStemMaskAsync(
                "spleeter-acc",
                accModelPath,
                provider,
                allowTrtInitFallback,
                inputTensor,
                cancellationToken).ConfigureAwait(false);
            EnsureConsistentProvider(selectedProvider, accResult.SelectedProvider);

            progress?.Report(new StemSeparationProgress(
                (split * 2) + 2, totalSteps,
                (split + 1d) / numSplits, 1d));

            stftProcessor.OverlapAddMaskedBlock(
                block, vocalsResult.Mask, accResult.Mask,
                vocalsMono, accMono, windowSum, cancellationToken);
        }

        SpleeterStftProcessor.NormalizeOverlapAdd(vocalsMono, accMono, windowSum);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selected_provider"] = selectedProvider ?? provider.ToString()
        };

        if (!string.IsNullOrWhiteSpace(bootstrapDetail))
        {
            metadata["bootstrap_detail"] = bootstrapDetail;
        }

        return new SpleeterSeparation(
            vocalsMono,
            accMono,
            request.SampleRate,
            numSplits,
            metadata);
    }

    private sealed record StemMaskResult(
        float[] Mask,
        string SelectedProvider,
        string? BootstrapDetail);

    /// <summary>Honest provider check: any lease disagreement mid-run fails rather than lying.</summary>
    internal static void EnsureConsistentProvider(string? established, string observed)
    {
        if (established is not null && !string.Equals(established, observed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Spleeter session provider changed mid-separation "
                + $"({established} -> {observed}); refusing to report one provider for mixed execution.");
        }
    }

    private async Task<StemMaskResult> RunStemMaskAsync(
        string poolKey,
        string modelPath,
        ExecutionProviderKind provider,
        bool allowTrtInitFallback,
        DenseTensor<float> inputTensor,
        CancellationToken cancellationToken)
    {
        using OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
            .CreatePooledSingleAsync(
                poolKey,
                modelPath,
                provider,
                cancellationToken,
                allowTrtInitFallback: allowTrtInitFallback)
            .ConfigureAwait(false);

        // Bind by the model's actual input name: the sherpa-onnx spleeter export names its
        // input "x", not "input". Read it from session metadata so any single-input variant works.
        string inputName = ResolveSingleInputName(sessionLease.Session);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = sessionLease.Session.RunWithRetry(
            [NamedOnnxValue.CreateFromTensor(inputName, inputTensor)],
            cancellationToken: cancellationToken);

        return new StemMaskResult(
            outputs.First().AsTensor<float>().ToArray(),
            sessionLease.SelectedProvider,
            sessionLease.BootstrapDetail);
    }

    private static string ResolveSingleInputName(InferenceSession session)
    {
        if (session.InputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"Spleeter ONNX model must expose exactly one input, but found {session.InputMetadata.Count}: " +
                $"[{string.Join(", ", session.InputMetadata.Keys)}].");
        }

        return session.InputMetadata.Keys.First();
    }
}
