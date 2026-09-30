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
    string manifestPath,
    Func<StudioSettings>? settingsProvider = null) : INvidiaAfxRuntimeReadinessService
{
    public NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile)
    {
        // Defense in depth: even if this concrete service is constructed while the integration
        // is still stubbed, never claim Ready. Flip NvidiaAfxIntegration.IsStubbed() only after
        // real packaging URLs/checksums land AND NvAudioEffects create/run is verified on GPU.
        if (NvidiaAfxIntegration.IsStubbed())
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                NvidiaAfxIntegration.StubStatusLabel,
                null,
                NvidiaAfxIntegration.StubReason);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new NvidiaAfxRuntimeReadiness(false, "Unsupported OS", null, "NVIDIA AFX is Windows-only.");
        }

        StudioSettings? settings = settingsProvider?.Invoke();
        string? runtimeRoot = NvidiaAfxRuntimePathResolver.ResolveRuntimeRoot(
            componentStore,
            settings?.NvidiaAfxRuntimeDirectory);
        if (string.IsNullOrWhiteSpace(runtimeRoot))
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Not installed",
                null,
                "Runtime package is not installed. Accept the AFX license and install a verified package, " +
                "or set NvidiaAfxRuntimeDirectory / TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT to a local Maxine AFX install.");
        }

        if (!NvidiaAfxRuntimePathResolver.HasNativeLibrary(runtimeRoot))
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Missing native library",
                runtimeRoot,
                $"NvAudioEffects.dll was not found under '{runtimeRoot}'.");
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

        // External/local runtime roots may precede Trackdub-hosted downloads; still require
        // profile models on disk before claiming Ready.
        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(profile);
        bool hasRequiredModels = definition.RequiredModelRelativePaths.All(model =>
            File.Exists(Path.Join(runtimeRoot, model)));
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
