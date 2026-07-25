using Trackdub.Contracts;
using Trackdub.Composition.Runtime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.TestDoubles;
using Xunit;

namespace Trackdub.Composition.Tests.Runtime;

public sealed class RuntimePlanningPreferencesServiceTests
{
    [Fact]
    public async Task GetPreferredModelTier_UsesProfilerResolution()
    {
        var settings = new FakeStudioSettingsService();
        await settings.SaveAsync(StudioSettings.Default with { ModelTierPreference = "balanced" }, CancellationToken.None);
        var profiler = new FakeHardwareProfilerService { EffectiveModelTier = "turbo" };
        IRuntimePlanningPreferences preferences = new RuntimePlanningPreferencesService(settings, profiler);

        Assert.Equal(
            "turbo",
            await preferences.GetPreferredModelTierAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetBenchmarkEvidenceId_WhenProfilerBenchmarkFresh_ReturnsEvidence()
    {
        Guid evidenceId = Guid.NewGuid();
        var settings = new FakeStudioSettingsService();
        var profiler = new FakeHardwareProfilerService
        {
            ViewState = new HardwareProfilerViewState(
                new HardwareProfilerSnapshot(
                    evidenceId,
                    HardwareFingerprint.Create("windows", "x64", "GPU", 8L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024),
                    [],
                    new HardwarePresetRecommendation(
                        HardwareQualityPreset.Balanced,
                        "Balanced",
                        ["test"],
                        "balanced"),
                    DateTimeOffset.UtcNow),
                IsStale: false,
                HardwareQualityPreset.Balanced,
                null,
                null,
                false,
                evidenceId.ToString())
        };
        IRuntimePlanningPreferences preferences = new RuntimePlanningPreferencesService(settings, profiler);

        Assert.Equal(
            evidenceId.ToString(),
            await preferences.GetBenchmarkEvidenceIdAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetBenchmarkEvidenceId_WhenProfilerStale_ReturnsNull()
    {
        Guid evidenceId = Guid.NewGuid();
        var settings = new FakeStudioSettingsService();
        var profiler = new FakeHardwareProfilerService
        {
            ViewState = new HardwareProfilerViewState(
                new HardwareProfilerSnapshot(
                    evidenceId,
                    HardwareFingerprint.Create("windows", "x64", "GPU", 8L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024),
                    [],
                    new HardwarePresetRecommendation(
                        HardwareQualityPreset.Balanced,
                        "Balanced",
                        ["test"],
                        "balanced"),
                    DateTimeOffset.UtcNow),
                IsStale: true,
                HardwareQualityPreset.Balanced,
                null,
                null,
                false,
                evidenceId.ToString())
        };
        IRuntimePlanningPreferences preferences = new RuntimePlanningPreferencesService(settings, profiler);

        Assert.Null(await preferences.GetBenchmarkEvidenceIdAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetPreferredModelTier_WhenSettingsLoadFails_ReturnsNull()
    {
        var settings = new FakeStudioSettingsService
        {
            LoadException = new InvalidOperationException("settings unavailable")
        };
        var profiler = new FakeHardwareProfilerService();
        IRuntimePlanningPreferences preferences = new RuntimePlanningPreferencesService(settings, profiler);

        Assert.Null(
            await preferences.GetPreferredModelTierAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetBenchmarkEvidenceId_WhenProfilerThrows_ReturnsNull()
    {
        var settings = new FakeStudioSettingsService();
        var profiler = new FakeHardwareProfilerService
        {
            GetViewStateException = new InvalidOperationException("profiler unavailable")
        };
        IRuntimePlanningPreferences preferences = new RuntimePlanningPreferencesService(settings, profiler);

        Assert.Null(await preferences.GetBenchmarkEvidenceIdAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetPreferredModelTier_WhenCancelled_ThrowsOperationCanceledException()
    {
        var settings = new FakeStudioSettingsService();
        var profiler = new FakeHardwareProfilerService();
        IRuntimePlanningPreferences preferences = new RuntimePlanningPreferencesService(settings, profiler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            preferences.GetPreferredModelTierAsync(cts.Token));
    }

    [Fact]
    public async Task GetBenchmarkEvidenceId_WhenCancelled_ThrowsOperationCanceledException()
    {
        var settings = new FakeStudioSettingsService();
        var profiler = new FakeHardwareProfilerService();
        IRuntimePlanningPreferences preferences = new RuntimePlanningPreferencesService(settings, profiler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            preferences.GetBenchmarkEvidenceIdAsync(cts.Token));
    }
}
