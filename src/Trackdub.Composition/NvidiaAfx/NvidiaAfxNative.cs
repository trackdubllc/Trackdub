using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.NvidiaAfx;

/// <summary>
/// P/Invoke bindings for NVIDIA Maxine AFX (<c>NVAudioEffects.dll</c>), matching
/// <c>nvAudioEffects.h</c> from the Maxine AFX Windows SDK.
/// </summary>
internal static class NvidiaAfxNative
{
    internal const string LibraryName = "NVAudioEffects";

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
    private static bool _resolverRegistered;
    private static string? _loadedFrom;
    private static string? _libraryPath;

    public static void EnsureLoaded(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        lock (SyncRoot)
        {
            if (_loaded)
            {
                return;
            }

            string? libraryPath = NvidiaAfxRuntimeLayout.ResolveNativeLibraryPath(runtimeRoot);
            if (libraryPath is null)
            {
                throw new FileNotFoundException(
                    "NVIDIA AFX native library (NVAudioEffects.dll) not found in runtime package.",
                    Path.Join(runtimeRoot, "NVAudioEffects.dll"));
            }

            _libraryPath = libraryPath;
            EnsureDllImportResolverRegistered();

            // Preload Maxine feature DLLs by absolute path so CreateEffect can resolve them
            // without mutating the process-wide DLL search directory (CodeQL / SetDllDirectory).
            PreloadWindowsNativeDependencies(runtimeRoot);

            NativeLibrary.Load(libraryPath);
            _loaded = true;
            _loadedFrom = runtimeRoot;
        }
    }

    public static string? LoadedFrom => _loadedFrom;

    /// <summary>
    /// Loads <c>features/*/bin/*.dll</c> (and sidecar <c>nvafx*.dll</c>) via managed
    /// <see cref="NativeLibrary.Load(string)"/> before the core AFX library. Already-mapped
    /// modules remain available when Maxine later LoadLibrary's them by basename.
    /// </summary>
    private static void PreloadWindowsNativeDependencies(string runtimeRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (string dependencyPath in NvidiaAfxRuntimeLayout.EnumerateFeatureNativeLibraryPaths(runtimeRoot))
        {
            try
            {
                NativeLibrary.Load(dependencyPath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                // Best-effort: invalid/missing feature payloads are reported by CreateEffect/Load.
            }
        }
    }

    private static void EnsureDllImportResolverRegistered()
    {
        if (_resolverRegistered)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NvidiaAfxNative).Assembly, ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
            // Another owner already installed a resolver for this assembly.
        }

        _resolverRegistered = true;
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!IsNvidiaAfxLibraryName(libraryName) || string.IsNullOrWhiteSpace(_libraryPath))
        {
            return IntPtr.Zero;
        }

        return NativeLibrary.Load(_libraryPath);
    }

    private static bool IsNvidiaAfxLibraryName(string libraryName) =>
        libraryName.Equals(NvidiaAfxNative.LibraryName, StringComparison.OrdinalIgnoreCase)
        || libraryName.Equals(NvidiaAfxNative.LibraryName + ".dll", StringComparison.OrdinalIgnoreCase)
        || libraryName.Equals("NvAudioEffects", StringComparison.OrdinalIgnoreCase)
        || libraryName.Equals("NvAudioEffects.dll", StringComparison.OrdinalIgnoreCase);
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
