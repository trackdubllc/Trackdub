using Trackdub.Contracts;

namespace Trackdub.Composition.NvidiaAfx;

/// <summary>
/// Probes whether a local AFX runtime can actually load the native library and create/load
/// an effect. Filename presence alone must not be treated as Ready.
/// </summary>
public interface INvidiaAfxEffectProbe
{
    NvidiaAfxEffectProbeResult Probe(
        string runtimeRoot,
        NvidiaAfxProfileDefinition profile,
        int inputSampleRate,
        string? architectureBucket = null);
}

public sealed record NvidiaAfxEffectProbeResult(
    bool Succeeded,
    string? FailureReason,
    int? OutputSampleRate);

/// <summary>
/// Default probe: creates and loads a Maxine effect session, then disposes it.
/// </summary>
public sealed class NvidiaAfxSessionEffectProbe : INvidiaAfxEffectProbe
{
    public static NvidiaAfxSessionEffectProbe Instance { get; } = new();

    public NvidiaAfxEffectProbeResult Probe(
        string runtimeRoot,
        NvidiaAfxProfileDefinition profile,
        int inputSampleRate,
        string? architectureBucket = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            using NvidiaAfxSession session = NvidiaAfxSession.Create(
                profile,
                runtimeRoot,
                inputSampleRate,
                intensityRatio: profile.SupportsIntensityRatio ? 1.0f : 0f,
                architectureBucket);
            return new NvidiaAfxEffectProbeResult(true, null, session.OutputSampleRate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new NvidiaAfxEffectProbeResult(
                false,
                $"AFX native probe failed for profile '{profile.Selector}': {ex.Message}",
                null);
        }
    }
}
