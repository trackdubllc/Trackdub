using System.Diagnostics;
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
    // Names from nvAudioEffects.h (SDK 2.x): NVAFX_PARAM_NUM_SAMPLES_PER_INPUT_FRAME / _OUTPUT_FRAME.
    public const string NumInputSamplesPerFrame = "num_samples_per_input_frame";
    public const string NumOutputSamplesPerFrame = "num_samples_per_output_frame";
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
                EnsureSameRuntimeRoot(_loadedFrom, runtimeRoot);
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
            // This throws when the runtime is incomplete, and the process is only marked loaded
            // afterwards, so pointing at a fixed folder later can still succeed.
            PreloadWindowsNativeDependencies(runtimeRoot);

            NativeLibrary.Load(libraryPath);
            _loaded = true;
            _loadedFrom = runtimeRoot;
        }
    }

    /// <summary>
    /// Native libraries cannot be unloaded safely, so once a runtime is loaded the process keeps it.
    /// Pairing that runtime's binaries with another folder's models would be a mixed runtime.
    /// </summary>
    internal static void EnsureSameRuntimeRoot(string? loadedFrom, string requestedRoot)
    {
        if (loadedFrom is null
            || string.Equals(NormalizeRoot(loadedFrom), NormalizeRoot(requestedRoot), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"NVIDIA AFX is already loaded from '{loadedFrom}' in this process and cannot switch runtimes. " +
            $"Restart Trackdub to use '{requestedRoot}'.");
    }

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static string? LoadedFrom => _loadedFrom;

    /// <summary>
    /// Loads the SDK's third-party DLLs (<c>bin/external</c>) and then <c>features/*/bin/*.dll</c>
    /// (plus sidecar <c>nvafx*.dll</c>) via managed <see cref="NativeLibrary.Load(string)"/> before
    /// the core AFX library. Already-mapped modules remain available when Maxine later
    /// LoadLibrary's them by basename; feature DLLs only resolve their CUDA/TensorRT dependencies
    /// this way because <c>bin/external/*/bin</c> is not on the DLL search path.
    /// </summary>
    private static void PreloadWindowsNativeDependencies(string runtimeRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string[] externals = [.. NvidiaAfxRuntimeLayout.EnumerateExternalDependencyPaths(runtimeRoot)];

        // The SDK's CUDA/TensorRT copies become the process-wide module for those names, so do not
        // quietly displace a different copy (for example ONNX Runtime's) that is already loaded.
        string? conflict = FindConflictingModule(externals, LoadedModulePaths());
        if (conflict is not null)
        {
            throw new InvalidOperationException(conflict);
        }

        List<string> failed =
        [
            .. PreloadAll(externals),
            .. PreloadAll(NvidiaAfxRuntimeLayout.EnumerateFeatureNativeLibraryPaths(runtimeRoot)),
        ];
        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"NVIDIA AFX runtime '{runtimeRoot}' has native libraries that could not be loaded: " +
                $"{string.Join(", ", failed.Select(path => Path.GetFileName(path)))}.");
        }
    }

    /// <summary>
    /// Describes the first dependency whose file name is already loaded from a different folder, or
    /// null when nothing would be displaced.
    /// </summary>
    internal static string? FindConflictingModule(IEnumerable<string> dependencyPaths, IEnumerable<string> loadedModulePaths)
    {
        ILookup<string, string> loadedByName = loadedModulePaths.ToLookup(
            path => Path.GetFileName(path),
            StringComparer.OrdinalIgnoreCase);

        foreach (string dependency in dependencyPaths)
        {
            string directory = NormalizeRoot(Path.GetDirectoryName(dependency) ?? string.Empty);
            string? other = loadedByName[Path.GetFileName(dependency)].FirstOrDefault(loaded =>
                !string.Equals(
                    NormalizeRoot(Path.GetDirectoryName(loaded) ?? string.Empty),
                    directory,
                    StringComparison.OrdinalIgnoreCase));
            if (other is not null)
            {
                return $"NVIDIA AFX needs its own {Path.GetFileName(dependency)}, but a different copy is already " +
                       $"loaded from '{other}'. Loading both could break GPU inference elsewhere in Trackdub, so AFX " +
                       "was not started.";
            }
        }

        return null;
    }

    private static List<string> LoadedModulePaths()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.Modules
                .Cast<ProcessModule>()
                .Select(module => module.FileName)
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Without a module list the conflict check cannot run; the load itself still reports failures.
            return [];
        }
    }

    // Retries until a pass makes no progress, because the DLLs depend on each other (cuBLAS needs
    // cuBLASLt) and the enumeration order is not a dependency order. Returns what never loaded.
    private static List<string> PreloadAll(IEnumerable<string> dependencyPaths)
    {
        List<string> pending = [.. dependencyPaths];
        while (pending.Count > 0)
        {
            int before = pending.Count;
            pending.RemoveAll(TryPreload);
            if (pending.Count == before)
            {
                break;
            }
        }

        return pending;
    }

    private static bool TryPreload(string dependencyPath)
    {
        try
        {
            NativeLibrary.Load(dependencyPath);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return false;
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
