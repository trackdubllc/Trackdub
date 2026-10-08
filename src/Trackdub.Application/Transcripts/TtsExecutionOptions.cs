namespace Trackdub.Application.Transcripts;

/// <summary>
/// Execution tuning knobs for the TTS stage, resolved per workspace from settings plus
/// hardware-derived caps. Kept separate from <see cref="TtsTimingOptions"/> because timing
/// knobs are audio-correctness settings while these affect throughput and resource usage only.
/// </summary>
/// <param name="ConfiguredMaxConcurrency">
/// User/host-configured maximum degree of parallelism for TTS synthesis, before the
/// VRAM-aware cap. Null means the historical default (4).
/// </param>
/// <param name="MaxAcceleratorVramMb">
/// Largest dedicated GPU adapter memory in MB, or 0 when unknown/unavailable (CPU-only
/// hosts, detection failure). Derived from the same hardware profile the planner uses.
/// </param>
public sealed record TtsExecutionOptions(
    int? ConfiguredMaxConcurrency,
    long MaxAcceleratorVramMb)
{
    /// <summary>Historical default degree of parallelism for TTS synthesis (pre-D2 constant).</summary>
    public const int LegacyMaxConcurrency = 4;

    /// <summary>Upper bound for any configured concurrency value.</summary>
    public const int AbsoluteMaxConcurrency = 8;

    /// <summary>Upper bound for the VRAM-derived concurrency cap.</summary>
    public const int MaxVramDerivedConcurrency = 8;

    /// <summary>
    /// Minimum degree of parallelism: sequential synthesis. A VRAM budget that cannot cover
    /// one worker still synthesizes one segment at a time; it never fails the run.
    /// </summary>
    public const int MinConcurrency = 1;

    /// <summary>
    /// Approximate additional accelerator/activation memory per concurrent in-flight TTS
    /// synthesis, in MB. Grounded on the reference machine at degree 4: Kokoro peak process
    /// working set was ~0.8 GB, and ORT arena growth scales with concurrent sessions.
    /// Deliberately conservative: underestimated budgets must never raise the cap.
    /// </summary>
    public const long PerWorkerVramMb = 512;

    /// <summary>One-worker VRAM budget for the "small" class: Kokoro-82M (~0.5 GB on disk) plus activations.</summary>
    public const long SmallModelVramMb = 1024;

    /// <summary>One-worker VRAM budget for the "medium" class: CosyVoice-300M (~2.4 GB) or Chatterbox (~3.1 GB) on disk, plus activations.</summary>
    public const long MediumModelVramMb = 4096;

    /// <summary>One-worker VRAM budget for the "large" class: Qwen3-TTS-1.7B (~13 GB on disk) plus activations.</summary>
    public const long LargeModelVramMb = 16384;

    public static TtsExecutionOptions Default { get; } = new(
        ConfiguredMaxConcurrency: null,
        MaxAcceleratorVramMb: 0);

    /// <summary>
    /// Effective degree of parallelism for a TTS run: the configured value clamped to
    /// [<see cref="MinConcurrency"/>, <see cref="AbsoluteMaxConcurrency"/>], then capped by
    /// the VRAM-aware bound for the model class actually routed. When accelerator memory is
    /// unknown (0), the configured value applies unchanged — the cap only ever tightens.
    /// </summary>
    /// <param name="modelAlias">Resolved TTS model alias for the run (normalized; may be null).</param>
    public int ResolveEffectiveConcurrency(string? modelAlias)
    {
        // Null or non-positive configured values fall back to the legacy default; the
        // composition root normalizes them, but the record is robust on its own.
        int configuredValue = ConfiguredMaxConcurrency is int value && value >= MinConcurrency
            ? value
            : LegacyMaxConcurrency;
        int configured = Math.Clamp(configuredValue, MinConcurrency, AbsoluteMaxConcurrency);

        if (MaxAcceleratorVramMb <= 0)
        {
            return configured;
        }

        return Math.Min(configured, ResolveVramBound(modelAlias));
    }

    /// <summary>VRAM-derived worker bound for the model class of <paramref name="modelAlias"/>.</summary>
    internal int ResolveVramBound(string? modelAlias)
    {
        long modelBudgetMb = ResolveModelVramBudgetMb(modelAlias);
        long available = MaxAcceleratorVramMb - modelBudgetMb;
        if (available <= 0)
        {
            return MinConcurrency;
        }

        return (int)Math.Clamp(available / PerWorkerVramMb, MinConcurrency, MaxVramDerivedConcurrency);
    }

    /// <summary>
    /// One-worker VRAM budget for the model class of <paramref name="modelAlias"/>,
    /// classified by alias (same alias the planner resolves). Unknown aliases that name no
    /// known medium or large model resolve to the small stock class.
    /// </summary>
    internal long ResolveModelVramBudgetMb(string? modelAlias)
    {
        if (IsLargeModelAlias(modelAlias))
        {
            return LargeModelVramMb;
        }

        if (IsMediumModelAlias(modelAlias))
        {
            return MediumModelVramMb;
        }

        return SmallModelVramMb;
    }

    private static bool IsLargeModelAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        string normalized = alias.Trim();
        return Qwen3TtsDefaults.IsLargeAlias(normalized);
    }

    private static bool IsMediumModelAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        string normalized = alias.Trim();
        return normalized.Equals("chatterbox-turbo", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("chatterbox", StringComparison.OrdinalIgnoreCase) ||
               VoiceCloningDefaults.IsVoiceCloningModelAlias(normalized) ||
               VoiceCloningDefaults.IsF5VoiceCloningModelAlias(normalized) ||
               Qwen3TtsDefaults.IsAnyQwen3Alias(normalized);
    }
}
