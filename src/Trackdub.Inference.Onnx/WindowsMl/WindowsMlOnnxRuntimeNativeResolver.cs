using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.WindowsMl;

internal static class WindowsMlOnnxRuntimeNativeResolver
{
    private static int initialized;

    public static void EnsureInitialized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (Interlocked.Exchange(ref initialized, 1) == 1)
        {
            return;
        }

        try
        {
            TryLoadOnnxRuntimeNative("onnxruntime.dll", out _);
            TryLoadOnnxRuntimeNative("onnxruntime_providers_shared.dll", out _);

            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
            // Another owner installed a resolver first. Continue; readiness still depends on the smoke test.
        }
    }

    private static nint ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (IsOnnxRuntimeLibraryName(libraryName) &&
            TryLoadOnnxRuntimeNative("onnxruntime.dll", out nint onnxRuntimeHandle))
        {
            return onnxRuntimeHandle;
        }

        if (IsOnnxRuntimeProvidersSharedLibraryName(libraryName) &&
            TryLoadOnnxRuntimeNative("onnxruntime_providers_shared.dll", out nint providersHandle))
        {
            return providersHandle;
        }

        return nint.Zero;
    }


    private static bool TryLoadOnnxRuntimeNative(string fileName, out nint handle)
    {
        if (TryLoadFromManagedPackageDirectory(fileName, out handle))
        {
            return true;
        }

        return TryLoadFromBaseDirectory(fileName, out handle);
    }

    private static bool TryLoadFromManagedPackageDirectory(string fileName, out nint handle)
    {
        handle = nint.Zero;
        string? assemblyDir = Path.GetDirectoryName(typeof(OrtEnv).Assembly.Location);
        if (string.IsNullOrWhiteSpace(assemblyDir))
        {
            return false;
        }

        string[] candidates =
        [
            Path.Combine(assemblyDir, fileName),
            Path.Combine(assemblyDir, "runtimes", "win-x64", "native", fileName),
            Path.Combine(assemblyDir, "runtimes", "win-arm64", "native", fileName)
        ];

        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                handle = NativeLibrary.Load(candidate);
                if (handle != nint.Zero)
                {
                    return true;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }

        return false;
    }

    private static bool TryLoadFromBaseDirectory(string fileName, out nint handle)
    {
        string candidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(candidate))
        {
            handle = nint.Zero;
            return false;
        }

        try
        {
            handle = NativeLibrary.Load(candidate);
            return handle != nint.Zero;
        }
        catch (DllNotFoundException)
        {
            handle = nint.Zero;
            return false;
        }
        catch (BadImageFormatException)
        {
            handle = nint.Zero;
            return false;
        }
    }

    private static bool IsOnnxRuntimeLibraryName(string libraryName) =>
        string.Equals(libraryName, "onnxruntime", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(libraryName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsOnnxRuntimeProvidersSharedLibraryName(string libraryName) =>
        string.Equals(libraryName, "onnxruntime_providers_shared", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(libraryName, "onnxruntime_providers_shared.dll", StringComparison.OrdinalIgnoreCase);
}
