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

/// <summary>
/// Post-stub readiness evaluation for an already-resolved runtime root (DLL/models/native probe).
/// Separated so unit tests can cover the gate without flipping <see cref="NvidiaAfxIntegration.IsStubbed"/>.
/// </summary>
public sealed class NvidiaAfxInstalledRuntimeEvaluator(
    INvidiaAfxArchitectureDetector architectureDetector,
    string manifestPath,
    INvidiaAfxEffectProbe effectProbe)
{
    public NvidiaAfxRuntimeReadiness Evaluate(NvidiaAfxProfile profile, string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        if (!NvidiaAfxRuntimePathResolver.HasNativeLibrary(runtimeRoot))
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Missing native library",
                runtimeRoot,
                $"NVAudioEffects.dll was not found under '{runtimeRoot}'.");
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
        bool hasRequiredModels = NvidiaAfxRuntimeLayout.HasRequiredModels(
            runtimeRoot,
            definition.RequiredModels.Select(model => (model.FeatureFolder, model.ModelStem)),
            architecture);
        if (!hasRequiredModels)
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Missing model files",
                runtimeRoot,
                $"Required Maxine feature models are missing for profile '{profile}' " +
                $"(architecture '{architecture}'). Expected under features/<nvafx*>/models/.");
        }

        int inputSampleRate = definition.SupportedSampleRates[0];
        NvidiaAfxEffectProbeResult probe = effectProbe.Probe(
            runtimeRoot,
            definition,
            inputSampleRate,
            architecture);
        if (!probe.Succeeded)
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Native probe failed",
                runtimeRoot,
                probe.FailureReason ?? "AFX native library/effect probe failed.");
        }

        return new NvidiaAfxRuntimeReadiness(true, "Ready", runtimeRoot, null);
    }
}

public sealed class NvidiaAfxRuntimeReadinessService(
    ComponentStore componentStore,
    INvidiaAfxArchitectureDetector architectureDetector,
    string manifestPath,
    Func<StudioSettings>? settingsProvider = null,
    INvidiaAfxEffectProbe? effectProbe = null) : INvidiaAfxRuntimeReadinessService
{
    private readonly NvidiaAfxInstalledRuntimeEvaluator _evaluator = new(
        architectureDetector,
        manifestPath,
        effectProbe ?? NvidiaAfxSessionEffectProbe.Instance);

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

        return _evaluator.Evaluate(profile, runtimeRoot);
    }
}
