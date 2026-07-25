using Trackdub.Contracts;
using Trackdub.Infrastructure.Components;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.NvidiaAfx;

public sealed record NvidiaAfxRuntimeReadiness(
    bool IsReady,
    string StatusLabel,
    string? RuntimeRoot,
    string? FailureReason);

public interface INvidiaAfxRuntimeReadinessService
{
    NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile);
}

public sealed class NvidiaAfxRuntimeReadinessService(
    ComponentStore componentStore,
    INvidiaAfxArchitectureDetector architectureDetector,
    string manifestPath) : INvidiaAfxRuntimeReadinessService
{
    public NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NvidiaAfxRuntimeReadiness(false, "Unsupported OS", null, "NVIDIA AFX is Windows-only.");
        }

        string? runtimeRoot = componentStore.GetInstallPath(NvidiaAfxRuntimeDownloader.ComponentId);
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
        {
            return new NvidiaAfxRuntimeReadiness(false, "Not installed", null, "Runtime package is not installed.");
        }

        NvidiaAfxRuntimeManifest manifest;
        try
        {
            manifest = NvidiaAfxRuntimeManifestLoader.Load(manifestPath);
        }
        catch (Exception ex)
        {
            return new NvidiaAfxRuntimeReadiness(false, "Manifest error", runtimeRoot, ex.Message);
        }

        string architecture = architectureDetector.DetectArchitectureBucket();
        NvidiaAfxRuntimePackage? package = manifest.Packages
            .FirstOrDefault(candidate => string.Equals(candidate.Architecture, architecture, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Unsupported GPU",
                runtimeRoot,
                $"No AFX runtime package is available for architecture bucket '{architecture}'.");
        }

        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(profile);
        bool hasRequiredModels = definition.RequiredModelRelativePaths.All(model =>
            File.Exists(Path.Combine(runtimeRoot, model)));
        if (!hasRequiredModels)
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Missing model files",
                runtimeRoot,
                $"Required model files are missing for profile '{profile}'.");
        }

        return new NvidiaAfxRuntimeReadiness(true, "Ready", runtimeRoot, null);
    }
}
