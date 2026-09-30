using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Trackdub.Composition.NvidiaAfx;

/// <summary>
/// P/Invoke bindings for NVIDIA Maxine AFX (<c>NvAudioEffects.dll</c>), matching
/// <c>nvAudioEffects.h</c> from NVIDIA-Maxine/Maxine-AFX-SDK.
/// </summary>
internal static class NvidiaAfxNative
{
    private const string LibraryName = "NvAudioEffects";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int NvAFX_CreateEffect(
        string effectSelector,
        out IntPtr effectHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int NvAFX_CreateChainedEffect(
        string chainedSelector,
        out IntPtr effectHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvAFX_DestroyEffect(IntPtr effectHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int NvAFX_SetString(
        IntPtr effectHandle,
        string parameter,
        string value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int NvAFX_SetStringList(
        IntPtr effectHandle,
        string parameter,
        [In] string[] values,
        uint count);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int NvAFX_SetFloat(
        IntPtr effectHandle,
        string parameter,
        float value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int NvAFX_SetU32(
        IntPtr effectHandle,
        string parameter,
        uint value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int NvAFX_GetU32(
        IntPtr effectHandle,
        string parameter,
        out uint value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvAFX_Load(IntPtr effectHandle);

    /// <summary>
    /// Native signature is <c>const float** input, float** output</c>. Each array entry is a
    /// pointer to a planar channel buffer. AEC uses two inputs: near-end then far-end.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvAFX_Run(
        IntPtr effectHandle,
        [In] IntPtr[] input,
        [In] IntPtr[] output,
        uint numInputSamples,
        uint numInputChannels);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvAFX_Reset(IntPtr effectHandle);
}

internal static class NvidiaAfxNativeParameters
{
    public const string ModelPath = "model_path";
    public const string InputSampleRate = "input_sample_rate";
    public const string OutputSampleRate = "output_sample_rate";
    public const string IntensityRatio = "intensity_ratio";
    public const string NumInputSamplesPerFrame = "num_input_samples_per_frame";
    public const string NumOutputSamplesPerFrame = "num_output_samples_per_frame";
    public const string NumInputChannels = "num_input_channels";
    public const string NumOutputChannels = "num_output_channels";

    // Deprecated aliases retained for older SDK builds.
    public const string SamplesPerFrameLegacy = "num_samples_per_frame";
}

internal static class NvidiaAfxNativeLoader
{
    private static readonly object SyncRoot = new();
    private static bool _loaded;
    private static string? _loadedFrom;

    public static void EnsureLoaded(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        lock (SyncRoot)
        {
            if (_loaded)
            {
                return;
            }

            string libraryPath = Path.Join(runtimeRoot, "NvAudioEffects.dll");
            if (!File.Exists(libraryPath))
            {
                throw new FileNotFoundException("NVIDIA AFX native library not found in runtime package.", libraryPath);
            }

            NativeLibrary.Load(libraryPath);
            _loaded = true;
            _loadedFrom = runtimeRoot;
        }
    }

    public static string? LoadedFrom => _loadedFrom;
}

internal sealed class NvidiaAfxEffectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NvidiaAfxEffectHandle() : base(ownsHandle: true)
    {
    }

    public NvidiaAfxEffectHandle(IntPtr existingHandle) : base(ownsHandle: true)
    {
        SetHandle(existingHandle);
    }

    protected override bool ReleaseHandle() => NvidiaAfxNative.NvAFX_DestroyEffect(handle) == 0;
}
