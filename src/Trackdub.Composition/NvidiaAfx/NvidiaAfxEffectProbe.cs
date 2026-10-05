using System.Collections.Concurrent;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Components.NvidiaAfx;

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

/// <summary>
/// Remembers successful native probes so readiness checks do not create a GPU effect every time.
/// Concurrent callers for the same key share one probe, and failures are never cached. The key
/// includes the runtime root, effect, sample rate, architecture and the native library's write
/// time, so a different or replaced runtime is probed again.
/// </summary>
internal sealed class CachingNvidiaAfxEffectProbe(INvidiaAfxEffectProbe inner) : INvidiaAfxEffectProbe
{
    private readonly ConcurrentDictionary<ProbeKey, Lazy<NvidiaAfxEffectProbeResult>> _results = new();

    public NvidiaAfxEffectProbeResult Probe(
        string runtimeRoot,
        NvidiaAfxProfileDefinition profile,
        int inputSampleRate,
        string? architectureBucket = null)
    {
        var key = new ProbeKey(
            runtimeRoot,
            profile.Selector,
            inputSampleRate,
            architectureBucket,
            NativeLibraryStamp(runtimeRoot));
        Lazy<NvidiaAfxEffectProbeResult> entry = _results.GetOrAdd(
            key,
            _ => new Lazy<NvidiaAfxEffectProbeResult>(
                () => inner.Probe(runtimeRoot, profile, inputSampleRate, architectureBucket)));

        NvidiaAfxEffectProbeResult result = entry.Value;
        if (!result.Succeeded)
        {
            _results.TryRemove(key, out _);
        }

        return result;
    }

    private static long NativeLibraryStamp(string runtimeRoot)
    {
        string? path = NvidiaAfxRuntimeLayout.ResolveNativeLibraryPath(runtimeRoot);
        return path is null ? 0 : File.GetLastWriteTimeUtc(path).Ticks;
    }

    private readonly record struct ProbeKey(
        string RuntimeRoot,
        string Selector,
        int SampleRate,
        string? Architecture,
        long NativeLibraryStamp);
}
