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

        // 1. Forward STFT
        var (leftMag, leftPhase, targetFrames) = stftProcessor.Forward(request.Left);
        var (rightMag, rightPhase, _) = stftProcessor.Forward(request.Right);

        int numSplits = targetFrames / SpleeterStftProcessor.PadTo;
        int freqs = 1024;

        // 2. Build Input Tensor [2, num_splits, 512, 1024]
        var inputValues = new float[2 * numSplits * SpleeterStftProcessor.PadTo * freqs];
        int channelStride = numSplits * SpleeterStftProcessor.PadTo * freqs;

        Array.Copy(leftMag, 0, inputValues, 0, leftMag.Length);
        Array.Copy(rightMag, 0, inputValues, channelStride, rightMag.Length);

        var inputTensor = new DenseTensor<float>(inputValues, [2, numSplits, SpleeterStftProcessor.PadTo, freqs]);

        ExecutionProviderKind provider = request.Plan.ExecutionProvider ?? ExecutionProviderKind.Cpu;

        string vocalsModelPath = Path.Combine(request.ModelRootPath, "vocals.onnx");
        string accModelPath = Path.Combine(request.ModelRootPath, "accompaniment.onnx");

        string selectedProvider;
        string? bootstrapDetail;

        float[] vocalsMaskMag;
        float[] accMaskMag;

        progress?.Report(new StemSeparationProgress(0, 2, 0d, 1d));

        // 3. Run Vocals
        using (OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
                   .CreatePooledSingleAsync("spleeter-vocals", vocalsModelPath, provider, cancellationToken)
                   .ConfigureAwait(false))
        {
            selectedProvider = sessionLease.SelectedProvider;
            bootstrapDetail = sessionLease.BootstrapDetail;

            // Bind by the model's actual input name: the sherpa-onnx spleeter export names its
            // input "x", not "input". Read it from session metadata so any single-input variant works.
            string inputName = ResolveSingleInputName(sessionLease.Session);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = sessionLease.Session.RunWithRetry(
                [NamedOnnxValue.CreateFromTensor(inputName, inputTensor)]);

            var outputTensor = outputs.First().AsTensor<float>();
            vocalsMaskMag = outputTensor.ToArray();
        }

        progress?.Report(new StemSeparationProgress(1, 2, 0.5d, 1d));

        // 4. Run Accompaniment
        using (OnnxExecutionSessionFactory.SingleSessionLease sessionLease = await OnnxExecutionSessionFactory
                   .CreatePooledSingleAsync("spleeter-acc", accModelPath, provider, cancellationToken)
                   .ConfigureAwait(false))
        {
            string inputName = ResolveSingleInputName(sessionLease.Session);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = sessionLease.Session.RunWithRetry(
                [NamedOnnxValue.CreateFromTensor(inputName, inputTensor)]);

            var outputTensor = outputs.First().AsTensor<float>();
            accMaskMag = outputTensor.ToArray();
        }

        progress?.Report(new StemSeparationProgress(2, 2, 1d, 1d));

        // 5. Soft-masking and IFFT
        float[] vocalsLeftMasked = new float[leftMag.Length];
        float[] vocalsRightMasked = new float[rightMag.Length];
        float[] accLeftMasked = new float[leftMag.Length];
        float[] accRightMasked = new float[rightMag.Length];

        for (int i = 0; i < leftMag.Length; i++)
        {
            float vL = vocalsMaskMag[i];
            float vR = vocalsMaskMag[channelStride + i];
            float aL = accMaskMag[i];
            float aR = accMaskMag[channelStride + i];

            // v² / (v² + a² + eps)
            float eps = 1e-10f;
            float denomL = vL * vL + aL * aL + eps;
            float denomR = vR * vR + aR * aR + eps;

            float maskVocalsL = (vL * vL) / denomL;
            float maskVocalsR = (vR * vR) / denomR;

            float maskAccL = (aL * aL) / denomL;
            float maskAccR = (aR * aR) / denomR;

            vocalsLeftMasked[i] = leftMag[i] * maskVocalsL;
            vocalsRightMasked[i] = rightMag[i] * maskVocalsR;

            accLeftMasked[i] = leftMag[i] * maskAccL;
            accRightMasked[i] = rightMag[i] * maskAccR;
        }

        float[] vocalsLeft = stftProcessor.Inverse(vocalsLeftMasked, leftPhase, targetFrames, originalLength);
        float[] vocalsRight = stftProcessor.Inverse(vocalsRightMasked, rightPhase, targetFrames, originalLength);
        float[] accLeft = stftProcessor.Inverse(accLeftMasked, leftPhase, targetFrames, originalLength);
        float[] accRight = stftProcessor.Inverse(accRightMasked, rightPhase, targetFrames, originalLength);

        // Mix to mono
        float[] vocalsMono = new float[originalLength];
        float[] accMono = new float[originalLength];

        for (int i = 0; i < originalLength; i++)
        {
            vocalsMono[i] = (vocalsLeft[i] + vocalsRight[i]) * 0.5f;
            accMono[i] = (accLeft[i] + accRight[i]) * 0.5f;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selected_provider"] = selectedProvider
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
