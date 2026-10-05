using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.DeepFilterNet;

/// <summary>
/// Encoder outputs for one window, converted to frame-major flat arrays (frame f occupies a
/// contiguous segment) so the streaming engine can run the decoders over gate-homogeneous
/// sub-runs while carrying recurrent state exactly like native libDF.
/// </summary>
internal sealed record DeepFilterNetEncoderResult(
    float[] Emb,   // [windowFrames, 512]
    float[] E3,    // [windowFrames, 64*8]
    float[] E2,    // [windowFrames, 64*8]
    float[] E1,    // [windowFrames, 64*16]
    float[] E0,    // [windowFrames, 64*32]
    float[] C0,    // [windowFrames, 64*96]
    float[] Lsnr,  // [windowFrames]
    int WindowFrames);

/// <summary>
/// Runs the three transformed DeepFilterNet3 ONNX models window by window while carrying the
/// GRU hidden states. The models were rewritten by
/// <see cref="DeepFilterNetOnnxGraphTransform"/> so every GRU exposes its <c>initial_h</c>
/// (graph input) and <c>Y_h</c> (graph output); the causal input pads were removed, so the
/// caller supplies true lookback rows.
/// </summary>
internal static class DeepFilterNetOnnxInference
{
    internal const int EncoderLookbackRows = 2;
    internal const int DfC0LookbackRows = 4;

    /// <summary>
    /// Runs the encoder over <c>windowFrames</c> rows plus lookback. The encoder always runs
    /// for every active frame (native <c>process_raw</c>); decoder skipping is decided per
    /// frame from <see cref="DeepFilterNetEncoderResult.Lsnr"/>.
    /// </summary>
    /// <param name="featErb">[1,1,W+2,32] feature rows: 2 true lookback rows + W window rows.</param>
    /// <param name="featSpec">[1,2,W+2,96] complex feature rows (same layout).</param>
    public static DeepFilterNetEncoderResult RunEncoderWindow(
        DeepFilterNetModelSessions sessions,
        float[,,,] featErb,
        float[,,,] featSpec,
        DeepFilterNetRecurrentState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(featErb);
        ArgumentNullException.ThrowIfNull(featSpec);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        int feedFrames = featErb.GetLength(2);
        int windowFrames = feedFrames - EncoderLookbackRows;
        if (windowFrames < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(featErb), $"Encoder feed must include {EncoderLookbackRows} lookback rows plus at least 1 window row, got {feedFrames}.");
        }

        int erbElements = feedFrames * DeepFilterNetSignalProcessor.ErbBands;
        int specElements = 2 * feedFrames * DeepFilterNetSignalProcessor.NbDf;
        var flatErb = new float[erbElements];
        var flatSpec = new float[specElements];
        Buffer.BlockCopy(featErb, 0, flatErb, 0, erbElements * sizeof(float));
        Buffer.BlockCopy(featSpec, 0, flatSpec, 0, specElements * sizeof(float));

        var encInputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("feat_erb",
                new DenseTensor<float>(flatErb, [1, 1, feedFrames, DeepFilterNetSignalProcessor.ErbBands])),
            NamedOnnxValue.CreateFromTensor("feat_spec",
                new DenseTensor<float>(flatSpec, [1, 2, feedFrames, DeepFilterNetSignalProcessor.NbDf])),
            NamedOnnxValue.CreateFromTensor(sessions.EncStateInputs[0],
                new DenseTensor<float>(state.Enc, [1, 1, DeepFilterNetOnnxGraphTransform.GruHiddenSize])),
        };

        using var encOutputs = sessions.Enc.Session.RunWithRetry(
            encInputs, cancellationToken: cancellationToken);

        Tensor<float> emb = GetOutputTensor(encOutputs, "emb");
        Tensor<float> c0 = GetOutputTensor(encOutputs, "c0");
        Tensor<float> e0 = GetOutputTensor(encOutputs, "e0");
        Tensor<float> e1 = GetOutputTensor(encOutputs, "e1");
        Tensor<float> e2 = GetOutputTensor(encOutputs, "e2");
        Tensor<float> e3 = GetOutputTensor(encOutputs, "e3");
        CopyStateOutput(encOutputs, sessions.EncStateOutputs[0], state.Enc);

        // The pad-neutralized encoder emits exactly the W window rows (validated against the
        // native render); assert the time dimension instead of assuming it.
        return new DeepFilterNetEncoderResult(
            ToFrameMajor(emb, windowFrames, 512, "enc output 'emb'"),
            ToFrameMajor(e3, windowFrames, 64 * 8, "enc output 'e3'"),
            ToFrameMajor(e2, windowFrames, 64 * 8, "enc output 'e2'"),
            ToFrameMajor(e1, windowFrames, 64 * 16, "enc output 'e1'"),
            ToFrameMajor(e0, windowFrames, 64 * 32, "enc output 'e0'"),
            ToFrameMajor(c0, windowFrames, 64 * DeepFilterNetSignalProcessor.NbDf, "enc output 'c0'"),
            ExtractFlat(GetOutputTensor(encOutputs, "lsnr"), windowFrames, "enc output 'lsnr'"),
            windowFrames);
    }

    /// <summary>
    /// Runs the ERB decoder over a gate-homogeneous run of frames. Skipped runs never call
    /// this, so their GRU states stay frozen exactly as native does when it skips the stage.
    /// </summary>
    /// <param name="encoder">Full-window encoder result.</param>
    /// <param name="offsetFrames">First frame of the run within the window.</param>
    /// <param name="runFrames">Run length.</param>
    public static float[,,,] RunErbDecoderWindow(
        DeepFilterNetModelSessions sessions,
        DeepFilterNetEncoderResult encoder,
        int offsetFrames,
        int runFrames,
        DeepFilterNetRecurrentState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(offsetFrames);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(runFrames, 0);
        if (offsetFrames + runFrames > encoder.WindowFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runFrames), $"Run [{offsetFrames}, {offsetFrames + runFrames}) exceeds window of {encoder.WindowFrames} frames.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var erbDecInputs = new List<NamedOnnxValue>(7)
        {
            NamedOnnxValue.CreateFromTensor("emb", FrameMajorTensor(encoder.Emb, 512, offsetFrames, runFrames)),
            NamedOnnxValue.CreateFromTensor("e3", ChannelFirstTensor(encoder.E3, 64, 8, offsetFrames, runFrames)),
            NamedOnnxValue.CreateFromTensor("e2", ChannelFirstTensor(encoder.E2, 64, 8, offsetFrames, runFrames)),
            NamedOnnxValue.CreateFromTensor("e1", ChannelFirstTensor(encoder.E1, 64, 16, offsetFrames, runFrames)),
            NamedOnnxValue.CreateFromTensor("e0", ChannelFirstTensor(encoder.E0, 64, 32, offsetFrames, runFrames)),
            NamedOnnxValue.CreateFromTensor(sessions.ErbStateInputs[0],
                new DenseTensor<float>(state.Erb0, [1, 1, DeepFilterNetOnnxGraphTransform.GruHiddenSize])),
            NamedOnnxValue.CreateFromTensor(sessions.ErbStateInputs[1],
                new DenseTensor<float>(state.Erb1, [1, 1, DeepFilterNetOnnxGraphTransform.GruHiddenSize])),
        };

        using var erbDecOutputs = sessions.ErbDec.Session.RunWithRetry(
            erbDecInputs, cancellationToken: cancellationToken);

        int maskElements = runFrames * DeepFilterNetSignalProcessor.ErbBands;
        float[] flatMask = ExtractFlat(
            GetOutputTensor(erbDecOutputs, "m"), maskElements, "erb_dec output 'm'");
        var erbGains = new float[1, 1, runFrames, DeepFilterNetSignalProcessor.ErbBands];
        Buffer.BlockCopy(flatMask, 0, erbGains, 0, maskElements * sizeof(float));
        CopyStateOutput(erbDecOutputs, sessions.ErbStateOutputs[0], state.Erb0);
        CopyStateOutput(erbDecOutputs, sessions.ErbStateOutputs[1], state.Erb1);
        return erbGains;
    }

    /// <summary>
    /// Runs the deep-filter decoder over a gate-homogeneous run of frames.
    /// </summary>
    /// <param name="c0LookbackFrameMajor">4 frame-major c0 rows ([4, 64*96]) from the most
    /// recent DF-executed frames (zeros when none yet); the conv path needs c0 rows for
    /// frames [w0-4, w0+W).</param>
    public static float[,,,,] RunDfDecoderWindow(
        DeepFilterNetModelSessions sessions,
        DeepFilterNetEncoderResult encoder,
        float[] c0LookbackFrameMajor,
        int offsetFrames,
        int runFrames,
        DeepFilterNetRecurrentState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(c0LookbackFrameMajor);
        ArgumentNullException.ThrowIfNull(state);
        if (c0LookbackFrameMajor.Length != DfC0LookbackRows * 64 * DeepFilterNetSignalProcessor.NbDf)
        {
            throw new ArgumentOutOfRangeException(
                nameof(c0LookbackFrameMajor),
                $"Expected {DfC0LookbackRows} frame-major c0 rows, got {c0LookbackFrameMajor.Length} values.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offsetFrames);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(runFrames, 0);
        if (offsetFrames + runFrames > encoder.WindowFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runFrames), $"Run [{offsetFrames}, {offsetFrames + runFrames}) exceeds window of {encoder.WindowFrames} frames.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Combine lookback + run rows into channel-first [1,64,R+4,96].
        int c0Band = DeepFilterNetSignalProcessor.NbDf;
        int combinedFrames = runFrames + DfC0LookbackRows;
        var c0Combined = new float[64 * combinedFrames * c0Band];
        for (int c = 0; c < 64; c++)
        {
            for (int r = 0; r < DfC0LookbackRows; r++)
            {
                Buffer.BlockCopy(
                    c0LookbackFrameMajor, ((r * 64 * c0Band) + (c * c0Band)) * sizeof(float),
                    c0Combined, ((c * combinedFrames) + r) * c0Band * sizeof(float),
                    c0Band * sizeof(float));
            }

            for (int f = 0; f < runFrames; f++)
            {
                Buffer.BlockCopy(
                    encoder.C0, (((offsetFrames + f) * 64 * c0Band) + (c * c0Band)) * sizeof(float),
                    c0Combined, ((c * combinedFrames) + DfC0LookbackRows + f) * c0Band * sizeof(float),
                    c0Band * sizeof(float));
            }
        }

        var dfDecInputs = new List<NamedOnnxValue>(4)
        {
            NamedOnnxValue.CreateFromTensor("emb", FrameMajorTensor(encoder.Emb, 512, offsetFrames, runFrames)),
            NamedOnnxValue.CreateFromTensor("c0", new DenseTensor<float>(c0Combined,
                [1, 64, combinedFrames, DeepFilterNetSignalProcessor.NbDf])),
            NamedOnnxValue.CreateFromTensor(sessions.DfStateInputs[0],
                new DenseTensor<float>(state.Df0, [1, 1, DeepFilterNetOnnxGraphTransform.GruHiddenSize])),
            NamedOnnxValue.CreateFromTensor(sessions.DfStateInputs[1],
                new DenseTensor<float>(state.Df1, [1, 1, DeepFilterNetOnnxGraphTransform.GruHiddenSize])),
        };

        using var dfDecOutputs = sessions.DfDec.Session.RunWithRetry(
            dfDecInputs, cancellationToken: cancellationToken);

        int coefElements = runFrames * DeepFilterNetSignalProcessor.NbDf * DeepFilterNetSignalProcessor.DfOrder * 2;
        float[] flatCoefs = ExtractFlat(
            GetOutputTensor(dfDecOutputs, "coefs"), coefElements, "df_dec output 'coefs'");
        float[,,,,] dfCoefs = DeepFilterNetSignalProcessor.UnpackDfCoefs(flatCoefs, runFrames);
        CopyStateOutput(dfDecOutputs, sessions.DfStateOutputs[0], state.Df0);
        CopyStateOutput(dfDecOutputs, sessions.DfStateOutputs[1], state.Df1);
        return dfCoefs;
    }

    /// <summary>
    /// Converts a channel-first encoder tensor [1,C,T,F] into frame-major flat
    /// [T,C*F] (frame f contiguous) for run slicing.
    /// </summary>
    private static float[] ToFrameMajor(Tensor<float> tensor, int expectedFrames, int frameSize, string description)
    {
        int[] dims = tensor.Dimensions.ToArray();
        // emb is the only rank-3 encoder output; special-case it here.
        if (dims.Length == 3 && dims[0] == 1 && dims[1] == expectedFrames && dims[2] == frameSize)
        {
            return ExtractFlat(tensor, expectedFrames * frameSize, description);
        }

        if (dims.Length != 4 || dims[0] != 1 || dims[2] != expectedFrames)
        {
            throw new InvalidOperationException(
                $"DeepFilterNet {description} has unexpected shape [{string.Join(",", dims)}]; expected [1,C,{expectedFrames},F].");
        }

        int channelCount = dims[1];
        int band = dims[3];
        if (channelCount * band != frameSize)
        {
            throw new InvalidOperationException(
                $"DeepFilterNet {description} frame size {channelCount}*{band} != {frameSize}.");
        }

        float[] flat = ExtractFlat(tensor, expectedFrames * frameSize, description);
        var frameMajor = new float[expectedFrames * frameSize];
        for (int f = 0; f < expectedFrames; f++)
        {
            for (int c = 0; c < channelCount; c++)
            {
                Buffer.BlockCopy(
                    flat, ((c * expectedFrames) + f) * band * sizeof(float),
                    frameMajor, ((f * frameSize) + (c * band)) * sizeof(float),
                    band * sizeof(float));
            }
        }

        return frameMajor;
    }

    /// <summary>Frame-major [offset, offset+frames) rows as [1,frames,inner].</summary>
    private static DenseTensor<float> FrameMajorTensor(float[] flat, int inner, int offsetFrames, int frames)
    {
        var slice = new float[frames * inner];
        Buffer.BlockCopy(flat, offsetFrames * inner * sizeof(float), slice, 0, slice.Length * sizeof(float));
        return new DenseTensor<float>(slice, [1, frames, inner]);
    }

    /// <summary>Frame-major rows as channel-first [1,channels,frames,band].</summary>
    private static DenseTensor<float> ChannelFirstTensor(
        float[] frameMajor, int channels, int band, int offsetFrames, int frames)
    {
        var tensor = new float[channels * frames * band];
        int frameSize = channels * band;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                Buffer.BlockCopy(
                    frameMajor, (((offsetFrames + f) * frameSize) + (c * band)) * sizeof(float),
                    tensor, (((c * frames) + f) * band) * sizeof(float),
                    band * sizeof(float));
            }
        }

        return new DenseTensor<float>(tensor, [1, channels, frames, band]);
    }

    private static void CopyStateOutput(
        IReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        string outputName,
        float[] destination)
    {
        Tensor<float> tensor = GetOutputTensor(outputs, outputName);
        if (tensor.Length != destination.Length)
        {
            throw new InvalidOperationException(
                $"DeepFilterNet state output '{outputName}' has {tensor.Length} values; expected {destination.Length}.");
        }

        if (tensor is DenseTensor<float> dense)
        {
            dense.Buffer.Span[..destination.Length].CopyTo(destination);
        }
        else
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = tensor.GetValue(i);
            }
        }
    }

    private static Tensor<float> GetOutputTensor(
        IReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        string name)
    {
        foreach (DisposableNamedOnnxValue output in outputs)
        {
            if (string.Equals(output.Name, name, StringComparison.Ordinal))
            {
                return output.AsTensor<float>();
            }
        }

        throw new InvalidOperationException(
            $"DeepFilterNet model did not produce expected output '{name}'. " +
            $"Available outputs: {string.Join(", ", outputs.Select(static o => o.Name))}.");
    }

    private static float[] ExtractFlat(Tensor<float> tensor, int expectedElements, string description)
    {
        if (tensor.Length != expectedElements)
        {
            throw new InvalidOperationException(
                $"DeepFilterNet {description} has {tensor.Length} elements; expected {expectedElements}.");
        }

        var flat = new float[expectedElements];
        if (tensor is DenseTensor<float> dense)
        {
            dense.Buffer.Span[..expectedElements].CopyTo(flat);
        }
        else
        {
            for (int i = 0; i < expectedElements; i++)
            {
                flat[i] = tensor.GetValue(i);
            }
        }

        return flat;
    }
}
