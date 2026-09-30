using System.Runtime.InteropServices;
using Trackdub.Contracts;

namespace Trackdub.Composition.NvidiaAfx;

internal sealed class NvidiaAfxSession : IDisposable
{
    private readonly NvidiaAfxEffectHandle _handle;
    private readonly string _selector;
    private readonly uint _numInputChannels;
    private readonly uint _numInputSamplesPerFrame;
    private readonly uint _numOutputSamplesPerFrame;
    private readonly bool _requiresFarEndReference;

    private NvidiaAfxSession(
        NvidiaAfxEffectHandle handle,
        string selector,
        uint numInputChannels,
        uint numInputSamplesPerFrame,
        uint numOutputSamplesPerFrame,
        bool requiresFarEndReference)
    {
        _handle = handle;
        _selector = selector;
        _numInputChannels = numInputChannels;
        _numInputSamplesPerFrame = numInputSamplesPerFrame;
        _numOutputSamplesPerFrame = numOutputSamplesPerFrame;
        _requiresFarEndReference = requiresFarEndReference;
    }

    public uint NumInputSamplesPerFrame => _numInputSamplesPerFrame;

    public uint NumOutputSamplesPerFrame => _numOutputSamplesPerFrame;

    public bool RequiresFarEndReference => _requiresFarEndReference;

    public static NvidiaAfxSession Create(
        NvidiaAfxProfileDefinition profile,
        string runtimeRoot,
        int sampleRate,
        float intensityRatio)
    {
        NvidiaAfxNativeLoader.EnsureLoaded(runtimeRoot);
        IntPtr effectHandle;
        int status;
        if (profile.IsChainedEffect)
        {
            status = NvidiaAfxNative.NvAFX_CreateChainedEffect(profile.Selector, out effectHandle);
        }
        else
        {
            status = NvidiaAfxNative.NvAFX_CreateEffect(profile.Selector, out effectHandle);
        }

        EnsureSuccess(status, profile.Selector, "CreateEffect");
        var safeHandle = new NvidiaAfxEffectHandle(effectHandle);
        try
        {
            if (profile.RequiredModelRelativePaths.Length > 0)
            {
                string[] modelPaths = profile.RequiredModelRelativePaths
                    .Select(relative => Path.Join(runtimeRoot, relative))
                    .ToArray();
                if (modelPaths.Length == 1)
                {
                    EnsureSuccess(
                        NvidiaAfxNative.NvAFX_SetString(
                            safeHandle.DangerousGetHandle(),
                            NvidiaAfxNativeParameters.ModelPath,
                            modelPaths[0]),
                        profile.Selector,
                        "Set model path");
                }
                else
                {
                    EnsureSuccess(
                        NvidiaAfxNative.NvAFX_SetStringList(
                            safeHandle.DangerousGetHandle(),
                            NvidiaAfxNativeParameters.ModelPath,
                            modelPaths,
                            (uint)modelPaths.Length),
                        profile.Selector,
                        "Set model path list");
                }
            }

            EnsureSuccess(
                NvidiaAfxNative.NvAFX_SetU32(
                    safeHandle.DangerousGetHandle(),
                    NvidiaAfxNativeParameters.InputSampleRate,
                    (uint)sampleRate),
                profile.Selector,
                "Set input sample rate");

            if (profile.SupportsIntensityRatio)
            {
                EnsureSuccess(
                    NvidiaAfxNative.NvAFX_SetFloat(
                        safeHandle.DangerousGetHandle(),
                        NvidiaAfxNativeParameters.IntensityRatio,
                        intensityRatio),
                    profile.Selector,
                    "Set intensity ratio");
            }

            EnsureSuccess(
                NvidiaAfxNative.NvAFX_Load(safeHandle.DangerousGetHandle()),
                profile.Selector,
                "Load");

            uint numInputChannels = QueryU32OrDefault(
                safeHandle,
                NvidiaAfxNativeParameters.NumInputChannels,
                profile.RequiresFarEndReference ? 2u : 1u);
            uint numInputSamples = QueryU32OrDefault(
                safeHandle,
                NvidiaAfxNativeParameters.NumInputSamplesPerFrame,
                fallbackParameter: NvidiaAfxNativeParameters.SamplesPerFrameLegacy,
                defaultValue: 480u);
            uint numOutputSamples = QueryU32OrDefault(
                safeHandle,
                NvidiaAfxNativeParameters.NumOutputSamplesPerFrame,
                fallbackParameter: NvidiaAfxNativeParameters.SamplesPerFrameLegacy,
                defaultValue: numInputSamples);

            if (profile.RequiresFarEndReference && numInputChannels < 2)
            {
                throw new InvalidOperationException(
                    $"NVIDIA AFX AEC profile '{profile.Selector}' reported {numInputChannels} input channel(s); expected at least 2 (near-end + far-end).");
            }

            return new NvidiaAfxSession(
                safeHandle,
                profile.Selector,
                numInputChannels,
                numInputSamples,
                numOutputSamples,
                profile.RequiresFarEndReference);
        }
        catch
        {
            safeHandle.Dispose();
            throw;
        }
    }

    public float[] Process(float[] nearEnd, float[]? farEnd = null)
    {
        ArgumentNullException.ThrowIfNull(nearEnd);

        if (_requiresFarEndReference)
        {
            ArgumentNullException.ThrowIfNull(farEnd);
        }

        int inputFrame = checked((int)_numInputSamplesPerFrame);
        int outputFrame = checked((int)_numOutputSamplesPerFrame);
        if (inputFrame <= 0 || outputFrame <= 0)
        {
            throw new InvalidOperationException("Invalid AFX frame size.");
        }

        int sampleCount = nearEnd.Length;
        if (farEnd is not null)
        {
            sampleCount = Math.Min(sampleCount, farEnd.Length);
        }

        // Align to whole input frames (pad tail with zeros like the SDK sample).
        int alignedInput = ((sampleCount + inputFrame - 1) / inputFrame) * inputFrame;
        float[] nearAligned = new float[alignedInput];
        Array.Copy(nearEnd, nearAligned, sampleCount);
        float[]? farAligned = null;
        if (_requiresFarEndReference)
        {
            farAligned = new float[alignedInput];
            Array.Copy(farEnd!, farAligned, sampleCount);
        }

        int outputLength = (alignedInput / inputFrame) * outputFrame;
        float[] output = new float[outputLength];
        float[] nearFrame = new float[inputFrame];
        float[] farFrame = _requiresFarEndReference ? new float[inputFrame] : [];
        float[] outFrame = new float[outputFrame];

        for (int inputOffset = 0, outputOffset = 0;
             inputOffset < alignedInput;
             inputOffset += inputFrame, outputOffset += outputFrame)
        {
            Array.Copy(nearAligned, inputOffset, nearFrame, 0, inputFrame);
            if (_requiresFarEndReference)
            {
                Array.Copy(farAligned!, inputOffset, farFrame, 0, inputFrame);
            }

            RunFrame(nearFrame, farFrame, outFrame);
            Array.Copy(outFrame, 0, output, outputOffset, outputFrame);
        }

        // Trim padding beyond the original sample count, mapped 1:1 for same-rate effects.
        // For rate-changing chained effects, return full produced frames.
        if (outputFrame == inputFrame && sampleCount < output.Length)
        {
            float[] trimmed = new float[sampleCount];
            Array.Copy(output, trimmed, sampleCount);
            return trimmed;
        }

        return output;
    }

    public void Reset()
    {
        EnsureSuccess(
            NvidiaAfxNative.NvAFX_Reset(_handle.DangerousGetHandle()),
            _selector,
            "Reset");
    }

    public void Dispose()
    {
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RunFrame(float[] nearFrame, float[] farFrame, float[] outFrame)
    {
        GCHandle nearHandle = GCHandle.Alloc(nearFrame, GCHandleType.Pinned);
        GCHandle farHandle = default;
        GCHandle outHandle = GCHandle.Alloc(outFrame, GCHandleType.Pinned);
        try
        {
            IntPtr[] inputs;
            if (_requiresFarEndReference)
            {
                farHandle = GCHandle.Alloc(farFrame, GCHandleType.Pinned);
                inputs = [nearHandle.AddrOfPinnedObject(), farHandle.AddrOfPinnedObject()];
            }
            else
            {
                inputs = [nearHandle.AddrOfPinnedObject()];
            }

            IntPtr[] outputs = [outHandle.AddrOfPinnedObject()];
            EnsureSuccess(
                NvidiaAfxNative.NvAFX_Run(
                    _handle.DangerousGetHandle(),
                    inputs,
                    outputs,
                    _numInputSamplesPerFrame,
                    _numInputChannels),
                _selector,
                "Run");
        }
        finally
        {
            if (farHandle.IsAllocated)
            {
                farHandle.Free();
            }

            nearHandle.Free();
            outHandle.Free();
        }
    }

    private static uint QueryU32OrDefault(
        NvidiaAfxEffectHandle handle,
        string parameter,
        uint defaultValue,
        string? fallbackParameter = null)
    {
        int status = NvidiaAfxNative.NvAFX_GetU32(
            handle.DangerousGetHandle(),
            parameter,
            out uint value);
        if (status == 0 && value != 0)
        {
            return value;
        }

        if (fallbackParameter is not null)
        {
            status = NvidiaAfxNative.NvAFX_GetU32(
                handle.DangerousGetHandle(),
                fallbackParameter,
                out value);
            if (status == 0 && value != 0)
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static void EnsureSuccess(int status, string selector, string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"NVIDIA AFX operation failed. Selector='{selector}', Operation='{operation}', Status={status}.");
        }
    }
}
