using Trackdub.Contracts;

namespace Trackdub.Composition.NvidiaAfx;

internal sealed class NvidiaAfxSession : IDisposable
{
    private readonly NvidiaAfxEffectHandle _handle;
    private readonly uint _channels;
    private readonly uint _samplesPerFrame;
    private readonly string _selector;

    private NvidiaAfxSession(
        NvidiaAfxEffectHandle handle,
        string selector,
        uint channels,
        uint samplesPerFrame)
    {
        _handle = handle;
        _selector = selector;
        _channels = channels;
        _samplesPerFrame = samplesPerFrame;
    }

    public static NvidiaAfxSession Create(
        NvidiaAfxProfileDefinition profile,
        string runtimeRoot,
        int sampleRate,
        int channels,
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
                    .Select(relative => Path.Combine(runtimeRoot, relative))
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
            uint frameSize = 0;
            int frameStatus = NvidiaAfxNative.NvAFX_GetU32(
                safeHandle.DangerousGetHandle(),
                NvidiaAfxNativeParameters.SamplesPerFrame,
                out frameSize);
            if (frameStatus != 0 || frameSize == 0)
            {
                frameSize = 480;
            }

            return new NvidiaAfxSession(safeHandle, profile.Selector, (uint)channels, frameSize);
        }
        catch
        {
            safeHandle.Dispose();
            throw;
        }
    }

    public float[] Process(float[] input)
    {
        float[] output = new float[input.Length];
        int frameSize = checked((int)_samplesPerFrame);
        int channels = checked((int)_channels);
        int stride = frameSize * channels;
        if (stride <= 0)
        {
            throw new InvalidOperationException("Invalid AFX frame size.");
        }

        for (int offset = 0; offset < input.Length; offset += stride)
        {
            int remaining = input.Length - offset;
            int currentFrameSamples = Math.Min(stride, remaining);
            float[] frameIn = new float[stride];
            float[] frameOut = new float[stride];
            Array.Copy(input, offset, frameIn, 0, currentFrameSamples);
            EnsureSuccess(
                NvidiaAfxNative.NvAFX_Run(
                    _handle.DangerousGetHandle(),
                    frameIn,
                    frameOut,
                    _samplesPerFrame,
                    _channels),
                _selector,
                "Run");
            Array.Copy(frameOut, 0, output, offset, currentFrameSamples);
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

    private static void EnsureSuccess(int status, string selector, string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"NVIDIA AFX operation failed. Selector='{selector}', Operation='{operation}', Status={status}.");
        }
    }
}
