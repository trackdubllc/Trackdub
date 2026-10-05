using System.Collections.Concurrent;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Components;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.NvidiaAfx;

public sealed record NvidiaAfxRuntimeReadiness(
    bool IsReady,
    string StatusLabel,
    string? RuntimeRoot,
    string? FailureReason,
    string? ArchitectureBucket = null);

public interface INvidiaAfxRuntimeReadinessService
{
    NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile);
}

/// <summary>
/// Post-stub readiness evaluation for an already-resolved runtime root (DLL/models/native probe).
/// Separated so unit tests can cover the gate without a real GPU runtime.
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

        // Maxine 3.x feature DLLs are required when a features/ tree is present. Legacy flat
        // models/-only fixtures (unit tests / older stages) skip this gate.
        if (NvidiaAfxRuntimeLayout.HasFeaturesDirectory(runtimeRoot)
            && !NvidiaAfxRuntimeLayout.HasRequiredFeatureLibraries(
                runtimeRoot,
                definition.RequiredFeatureFolders))
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Missing feature libraries",
                runtimeRoot,
                $"Required Maxine feature DLLs are missing for profile '{profile}'. " +
                "Expected features/<nvafx*>/bin/<nvafx*>.dll (or beside NVAudioEffects.dll).");
        }

        // Require models for every supported input rate so Ready is not rate-specific luck.
        bool hasRequiredModels = NvidiaAfxRuntimeLayout.HasRequiredModels(
            runtimeRoot,
            definition.AllRequiredModels.Select(model => (model.FeatureFolder, model.ModelStem)),
            architecture);
        if (!hasRequiredModels)
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Missing model files",
                runtimeRoot,
                $"Required Maxine feature models are missing for profile '{profile}' " +
                $"(architecture '{architecture}'). Expected under features/<nvafx*>/models/ " +
                "for each supported sample rate (for example denoiser_16k and denoiser_48k).");
        }

        int inputSampleRate = definition.PreferredProbeSampleRate;
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

        return new NvidiaAfxRuntimeReadiness(true, "Ready", runtimeRoot, null, architecture);
    }
}

public sealed class NvidiaAfxRuntimeReadinessService(
    ComponentStore componentStore,
    INvidiaAfxArchitectureDetector architectureDetector,
    string manifestPath,
    Func<StudioSettings>? settingsProvider = null,
    INvidiaAfxEffectProbe? effectProbe = null,
    Func<bool>? isStubbed = null) : INvidiaAfxRuntimeReadinessService
{
    private readonly Func<bool> _isStubbed = isStubbed ?? NvidiaAfxIntegration.IsStubbed;

    // Only Ready results are cached: they are the ones that cost a native GPU probe. A cached
    // runtime that later breaks falls back to DeepFilterNet when the enhancement run fails.
    private readonly ConcurrentDictionary<(NvidiaAfxProfile Profile, string RuntimeRoot), NvidiaAfxRuntimeReadiness> _readyCache = new();

    private readonly NvidiaAfxInstalledRuntimeEvaluator _evaluator = new(
        architectureDetector,
        manifestPath,
        effectProbe ?? NvidiaAfxSessionEffectProbe.Instance);

    public NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile)
    {
        if (_isStubbed())
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

        if (_readyCache.TryGetValue((profile, runtimeRoot), out NvidiaAfxRuntimeReadiness? cached))
        {
            return cached;
        }

        NvidiaAfxRuntimeReadiness readiness = _evaluator.Evaluate(profile, runtimeRoot);
        if (readiness.IsReady)
        {
            _readyCache[(profile, runtimeRoot)] = readiness;
        }

        return readiness;
    }
}
