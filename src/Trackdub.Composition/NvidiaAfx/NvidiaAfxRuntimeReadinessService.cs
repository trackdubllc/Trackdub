using System.Text.Json;
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

        // A stale adapter entry or two installed generations give several candidate architectures,
        // and the registry cannot say which one CUDA device 0 is. The first candidate whose models
        // exist and whose effects run on this machine wins; otherwise report the first failure.
        IReadOnlyList<string> candidates = architectureDetector.DetectArchitectureBuckets();
        if (candidates.Count == 0)
        {
            candidates = ["unsupported"];
        }

        NvidiaAfxRuntimeReadiness? firstFailure = null;
        foreach (string architecture in candidates)
        {
            NvidiaAfxRuntimeReadiness result = EvaluateArchitecture(profile, runtimeRoot, manifest, architecture);
            if (result.IsReady)
            {
                return result;
            }

            firstFailure ??= result;
        }

        return firstFailure!;
    }

    private NvidiaAfxRuntimeReadiness EvaluateArchitecture(
        NvidiaAfxProfile profile,
        string runtimeRoot,
        NvidiaAfxRuntimeManifest manifest,
        string architecture)
    {
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

        // Maxine 2.x/3.x feature DLLs are required when a features/ tree is present. Flat
        // models/-only installs (SDK 1.6, unit tests) have no feature DLLs to check.
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

        // Probe every supported rate: a corrupt or mismatched model for one rate would otherwise
        // report Ready and then fall back silently when that rate is used.
        foreach (int inputSampleRate in definition.SupportedSampleRates)
        {
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
                    (probe.FailureReason ?? "AFX native library/effect probe failed.") +
                    $" (architecture '{architecture}', {inputSampleRate} Hz)");
            }
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
    Func<bool>? isStubbed = null,
    Func<bool>? allowEarlyAccess = null) : INvidiaAfxRuntimeReadinessService
{
    private readonly Func<bool> _allowEarlyAccess = allowEarlyAccess ?? NvidiaAfxIntegration.AllowEarlyAccessEffects;

    private readonly Func<bool> _isStubbed = isStubbed ?? NvidiaAfxIntegration.IsStubbed;

    // The native probe is the expensive part (it creates a GPU effect), so only successful probes
    // are cached. Every call still re-checks the files and the detected architecture, so a removed
    // model or library, or a GPU change, is noticed immediately.
    private readonly NvidiaAfxInstalledRuntimeEvaluator _evaluator = new(
        architectureDetector,
        manifestPath,
        new CachingNvidiaAfxEffectProbe(effectProbe ?? NvidiaAfxSessionEffectProbe.Instance));

    // An Early Access effect runs only when the manifest records commercial terms for it. The
    // explicit opt-in is a separate, evaluation-only path for development and never changes the manifest.
    private NvidiaAfxRuntimeReadiness? CheckEarlyAccessEligibility(NvidiaAfxProfileDefinition definition)
    {
        try
        {
            if (NvidiaAfxRuntimeManifestLoader.Load(manifestPath).IsCommerciallyLicensed(definition.Selector))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new NvidiaAfxRuntimeReadiness(false, "Manifest error", null, ex.Message);
        }

        return _allowEarlyAccess()
            ? null
            : new NvidiaAfxRuntimeReadiness(
                false,
                "Early Access disabled",
                null,
                "This NVIDIA Early Access effect is licensed for evaluation only and is not enabled for production use.");
    }

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

        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(profile);
        if (definition.IsEarlyAccess)
        {
            NvidiaAfxRuntimeReadiness? blocked = CheckEarlyAccessEligibility(definition);
            if (blocked is not null)
            {
                return blocked;
            }
        }

        StudioSettings? settings;
        try
        {
            settings = settingsProvider?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Settings unavailable",
                null,
                $"Trackdub settings could not be read: {ex.Message}");
        }

        // Local runtimes are used under NVIDIA's license, so using one requires the same explicit
        // acceptance the installer asks for. Nothing is bundled or redistributed by Trackdub.
        if (settings is not { NvidiaAfxLicenseAccepted: true })
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "License not accepted",
                null,
                "Accept the NVIDIA AFX license in Settings before using NVIDIA AFX.");
        }

        string? runtimeRoot = NvidiaAfxRuntimePathResolver.ResolveRuntimeRoot(
            componentStore,
            settings.NvidiaAfxRuntimeDirectory);
        if (string.IsNullOrWhiteSpace(runtimeRoot))
        {
            return new NvidiaAfxRuntimeReadiness(
                false,
                "Not installed",
                null,
                "Runtime package is not installed. Install a verified package, or set " +
                "NvidiaAfxRuntimeDirectory / TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT to a local Maxine AFX install.");
        }

        return _evaluator.Evaluate(profile, runtimeRoot);
    }
}
