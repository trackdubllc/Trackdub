using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Trackdub.Composition.NvidiaAfx;

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

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
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

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvAFX_Run(
        IntPtr effectHandle,
        [In] float[] input,
        [Out] float[] output,
        uint samplesPerFrame,
        uint channels);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NvAFX_Reset(IntPtr effectHandle);
}

internal static class NvidiaAfxNativeParameters
{
    public const string ModelPath = "model_path";
    public const string InputSampleRate = "input_sample_rate";
    public const string IntensityRatio = "intensity_ratio";
    public const string SamplesPerFrame = "num_samples_per_frame";
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

            string libraryPath = Path.Combine(runtimeRoot, "NvAudioEffects.dll");
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
