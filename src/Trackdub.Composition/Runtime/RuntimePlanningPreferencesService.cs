using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Composition.Runtime;

public sealed class RuntimePlanningPreferencesService(
    IStudioSettingsService studioSettingsService,
    IHardwareProfilerService hardwareProfilerService) : IRuntimePlanningPreferences
{
    private readonly IStudioSettingsService studioSettingsService =
        studioSettingsService ?? throw new ArgumentNullException(nameof(studioSettingsService));

    private readonly IHardwareProfilerService hardwareProfilerService =
        hardwareProfilerService ?? throw new ArgumentNullException(nameof(hardwareProfilerService));

    public async Task<string?> GetPreferredModelTierAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            StudioSettings settings = await studioSettingsService
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);

            string tier = hardwareProfilerService.ResolveEffectiveModelTierPreference(settings);
            if (string.IsNullOrWhiteSpace(tier))
            {
                return null;
            }

            tier = tier.Trim();
            if (tier.Equals("experimental", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return tier;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<string?> GetBenchmarkEvidenceIdAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            HardwareProfilerViewState viewState = await hardwareProfilerService
                .GetViewStateAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!viewState.BenchmarkAvailableForPlanner ||
                string.IsNullOrWhiteSpace(viewState.EvidenceIdForPlanner))
            {
                return null;
            }

            return viewState.EvidenceIdForPlanner.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
