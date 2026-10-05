using Trackdub.Contracts;

namespace Trackdub.Composition.NvidiaAfx;

/// <summary>
/// Probes whether a local AFX runtime can actually load the native library, create and load an
/// effect, and run audio through it. Filename presence alone must not be treated as Ready.
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
/// Default probe: creates and loads a Maxine effect session, runs one frame through it, then
/// disposes it. A frame is run because creating and loading an effect does not prove it can process
/// audio. One frame is a smoke test, not a guarantee: a failed run later still falls back to DeepFilterNet.
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

            int frameSamples = checked((int)session.NumInputSamplesPerFrame);
            float[] frame = new float[frameSamples];
            for (int index = 0; index < frame.Length; index++)
            {
                frame[index] = 0.1f * MathF.Sin(2f * MathF.PI * 220f * index / inputSampleRate);
            }

            session.Process(frame, profile.RequiresFarEndReference ? new float[frameSamples] : null);
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
