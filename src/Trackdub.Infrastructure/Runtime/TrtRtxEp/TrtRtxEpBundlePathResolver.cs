namespace Trackdub.Infrastructure.Runtime.TrtRtxEp;

public static class TrtRtxEpBundlePathResolver
{
    public const string ProvidersRootSegment = "Providers";
    public const string ProviderFamilySegment = "trt-rtx";

    public static string ResolveRuntimeIdentifier()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux-x64";
        }

        throw new PlatformNotSupportedException("TensorRT RTX EP ABI v0.3.0 is supported on Windows and Linux x64 only.");
    }

    public static string GetInstallDirectory(string userDataRoot, string version, string cudaVariant, string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(cudaVariant);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        return Path.Combine(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(userDataRoot)),
            ProvidersRootSegment,
            ProviderFamilySegment,
            version,
            cudaVariant,
            runtimeIdentifier);
    }
}
