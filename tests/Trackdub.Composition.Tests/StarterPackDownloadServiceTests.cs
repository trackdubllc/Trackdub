using Trackdub.Application.Runtime;
using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackDownloadServiceTests
{
    [Fact]
    public async Task DownloadAsync_passes_hardware_variant_to_orchestrator()
    {
        var catalog = new StarterPackCatalog();
        var orchestrator = new CapturingDownloadOrchestrator();
        var profiler = new FakeHardwareProfilerService
        {
            ViewState = new HardwareProfilerViewState(
                Snapshot: null,
                IsStale: false,
                EffectivePreset: HardwareQualityPreset.Balanced,
                EffectiveRecommendation: new HardwarePresetRecommendation(
                    HardwareQualityPreset.Balanced,
                    "test",
                    ["unit-test"],
                    StarterPackStageMapping.ToHardwareProfileKey(StarterPackHardwareProfile.BalancedGpu)),
                OverridePresetKey: null,
                HasOverride: false,
                EvidenceIdForPlanner: null),
        };
        var service = new StarterPackDownloadService(
            catalog,
            orchestrator,
            new AlwaysReadyCloudCredentialReadiness(),
            profiler);

        StarterPackDownloadResult result = await service.DownloadAsync("basic", "default");

        Assert.True(result.Success);
        Assert.Contains(
            orchestrator.Calls,
            call => call.ModelId == "microsoft/Phi-4-mini-instruct-onnx" &&
                    string.Equals(call.VariantAlias, "gpu-int4", StringComparison.Ordinal));
    }

    private sealed class AlwaysReadyCloudCredentialReadiness : ICloudCredentialReadiness
    {
        public Task<CloudCredentialReadinessReport> EvaluateAsync(
            StarterPackCloudDefaults cloudDefaults,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudCredentialReadinessReport(true, [], null));
    }

    private sealed class CapturingDownloadOrchestrator : IModelDownloadOrchestrator
    {
        public List<(string ModelId, string? VariantAlias)> Calls { get; } = [];

        public IObservable<ModelStateChange> StateChanges { get; } = new EmptyObservable<ModelStateChange>();

        public Task<ModelDownloadResult> DownloadAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            DownloadAsync(modelId, variantAlias: null, progress, cancellationToken);

        public Task<ModelDownloadResult> DownloadAsync(
            string modelId,
            string? variantAlias,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((modelId, variantAlias));
            return Task.FromResult(new ModelDownloadResult(modelId, true, ModelCacheState.Installed, null));
        }

        public Task<ModelDownloadResult> RepairAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelDownloadResult(modelId, true, ModelCacheState.Installed, null));

        public Task<bool> UninstallAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class EmptyObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
