using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.TestDoubles;

/// <summary>
/// Test double for <see cref="IHardwareMatrix"/> that returns configurable rankings
/// per stage. Defaults to returning all devices with a score of 1.0 in input order.
/// </summary>
public sealed class FakeHardwareMatrix : IHardwareMatrix
{
    private readonly Dictionary<RuntimeStage, IReadOnlyList<ScoredDevice>> _stageResults = new();
    private Func<RuntimeStage, IReadOnlyList<DeviceEntry>, AffinityRule?, DeviceExclusionSet?, IReadOnlyList<ScoredDevice>>? _handler;

    /// <summary>
    /// Configures a fixed ranked result for a specific stage.
    /// </summary>
    public void SetResult(RuntimeStage stage, IReadOnlyList<ScoredDevice> result)
    {
        _stageResults[stage] = result;
    }

    /// <summary>
    /// Configures a handler that produces results dynamically based on inputs.
    /// Takes precedence over per-stage fixed results when set.
    /// </summary>
    public void SetHandler(
        Func<RuntimeStage, IReadOnlyList<DeviceEntry>, AffinityRule?, DeviceExclusionSet?, IReadOnlyList<ScoredDevice>> handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Number of times <see cref="RankDevices"/> has been called.
    /// </summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// The most recent stage passed to <see cref="RankDevices"/>.
    /// </summary>
    public RuntimeStage? LastStage { get; private set; }

    /// <summary>
    /// The most recent affinity rule passed to <see cref="RankDevices"/>.
    /// </summary>
    public AffinityRule? LastAffinityRule { get; private set; }

    public IReadOnlyList<ScoredDevice> RankDevices(
        RuntimeStage stage,
        IReadOnlyList<DeviceEntry> devices,
        AffinityRule? affinityRule = null,
        DeviceExclusionSet? exclusions = null)
    {
        CallCount++;
        LastStage = stage;
        LastAffinityRule = affinityRule;

        if (_handler is not null)
        {
            return _handler(stage, devices, affinityRule, exclusions);
        }

        if (_stageResults.TryGetValue(stage, out var result))
        {
            return result;
        }

        // Default: return all devices with a perfect score in input order.
        return devices
            .Select(d => new ScoredDevice(d, new HardwareScore(1.0, 1.0, 1.0, 0.0)))
            .ToList();
    }
}
