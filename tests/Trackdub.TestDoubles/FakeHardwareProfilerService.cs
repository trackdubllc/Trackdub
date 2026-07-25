using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.TestDoubles;

public sealed class FakeHardwareProfilerService : IHardwareProfilerService
{
    public HardwareProfilerViewState ViewState { get; set; } = new(
        null,
        false,
        HardwareQualityPreset.Balanced,
        null,
        null,
        false,
        null);

    public HardwareProfilerRunResult? RunResult { get; set; }

    public string EffectiveModelTier { get; set; } = "balanced";

    public Exception? GetViewStateException { get; set; }

    public Task<HardwareProfilerViewState> GetViewStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (GetViewStateException is not null)
        {
            throw GetViewStateException;
        }

        return Task.FromResult(ViewState);
    }

    public Task<HardwareProfilerRunResult> RunBenchmarkSuiteAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RunResult ?? HardwareProfilerRunResult.Failure("Profiler not configured."));

    public string ResolveEffectiveModelTierPreference(StudioSettings settings) => EffectiveModelTier;
}
