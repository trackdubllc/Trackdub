using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.TestDoubles;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif

namespace Trackdub.Inference.Tests;

/// <summary>
/// Skips when DirectML is unavailable (non-Windows hosts, CI without GPU catalog EP, etc.).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresDirectMlFactAttribute : FactAttribute
{
    public RequiresDirectMlFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        Skip = ResolveDirectMlSkip();
    }

    internal static string? ResolveDirectMlSkip()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "DirectML smoke requires Windows.";
        }

#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
        try
        {
            WindowsMlProviderRegistrationResult registration = WindowsMlProviderRegistrationPolicy.Shared
                .RegisterForReadinessAsync(ExecutionProviderKind.DirectMl, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            if (!registration.RegistrationSucceeded)
            {
                return string.IsNullOrWhiteSpace(registration.Detail)
                    ? "DirectML execution provider is not available: Windows ML catalog registration failed."
                    : $"DirectML execution provider is not available: {registration.Detail}";
            }
        }
        catch (Exception ex)
        {
            return $"DirectML execution provider is not available: {ex.Message}";
        }
#endif

        using SessionOptions options = new();
        if (!OnnxExecutionSessionFactory.TryAppendDirectMlProvider(options, out string? failureReason))
        {
            return string.IsNullOrWhiteSpace(failureReason)
                ? "DirectML execution provider is not available on this machine."
                : $"DirectML execution provider is not available: {failureReason}";
        }

        return null;
    }
}

/// <summary>
/// Bundled-model integration test that also requires a working DirectML catalog EP.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresDirectMlBundledModelFactAttribute : FactAttribute
{
    public RequiresDirectMlBundledModelFactAttribute(
        string relativePath,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        Skip = RequiresDirectMlFactAttribute.ResolveDirectMlSkip()
            ?? BundledModelSkipResolver.Resolve([relativePath]);
    }

    public RequiresDirectMlBundledModelFactAttribute(
        params string[] relativePaths)
    {
        Skip = RequiresDirectMlFactAttribute.ResolveDirectMlSkip()
            ?? BundledModelSkipResolver.Resolve(relativePaths);
    }
}
