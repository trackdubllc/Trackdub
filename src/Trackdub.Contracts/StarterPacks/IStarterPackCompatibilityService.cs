namespace Trackdub.Contracts.StarterPacks;

public interface IStarterPackCompatibilityService
{
    Task<StarterPackCompatibilityReport> EvaluateAsync(
        string packId,
        string profileId,
        StarterPackHardwareProfile? hardwareProfile = null,
        CancellationToken cancellationToken = default,
        bool skipProviderSmokeTest = false);
}

public sealed record StageCompatibilityEntry(
    string Stage,
    string Alias,
    string RequestedVariant,
    string RequestedExecutionProvider,
    string ResolvedVariant,
    string ResolvedExecutionProvider,
    bool FallbackApplied,
    string? FallbackReason,
    bool Runnable)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Positive inventory line: what this stage will actually run.</summary>
    public string DescribeResolvedRuntime() =>
        $"{Alias}: {ResolvedVariant} on {ResolvedExecutionProvider}";

    /// <summary>
    /// User-facing fallback note. Does not claim "GPU unavailable" when the resolved
    /// path is still a GPU (e.g. preferred DirectML/TRT fell back to CUDA fp16).
    /// Silent for Windows native-CUDA rewrites — the runtime plan already shows the DML path.
    /// </summary>
    public string DescribeFallback()
    {
        if (!FallbackApplied)
        {
            return string.Empty;
        }

        if (string.Equals(FallbackReason, "partial_offload_required", StringComparison.OrdinalIgnoreCase))
        {
            return $"Partial GPU offload required for {Alias}. Using {ResolvedVariant} on {ResolvedExecutionProvider}.";
        }

        if (string.Equals(FallbackReason, "native_cuda_disabled_on_windows", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (IsCpuProvider(ResolvedExecutionProvider))
        {
            return $"GPU path unavailable for {Alias}. Using {ResolvedVariant} on {ResolvedExecutionProvider}.";
        }

        if (!string.Equals(RequestedVariant, ResolvedVariant, StringComparison.OrdinalIgnoreCase) &&
            ExecutionProviderTokensMatch(RequestedExecutionProvider, ResolvedExecutionProvider))
        {
            return $"Preferred variant unavailable for {Alias}. Using {ResolvedVariant} on {ResolvedExecutionProvider}.";
        }

        if (!ExecutionProviderTokensMatch(RequestedExecutionProvider, ResolvedExecutionProvider) &&
            !IsAutoProvider(RequestedExecutionProvider))
        {
            return $"Preferred accelerator unavailable for {Alias}. Using {ResolvedVariant} on {ResolvedExecutionProvider}.";
        }

        return $"Preferred path unavailable for {Alias}. Using {ResolvedVariant} on {ResolvedExecutionProvider}.";
    }

    private static bool IsCpuProvider(string providerToken) =>
        NormalizeProviderToken(providerToken) is "cpu" or "onnxruntime-cpu";

    private static bool IsAutoProvider(string providerToken) =>
        string.IsNullOrWhiteSpace(providerToken) ||
        providerToken.Equals("auto", StringComparison.OrdinalIgnoreCase);

    private static bool ExecutionProviderTokensMatch(string left, string right) =>
        string.Equals(NormalizeProviderToken(left), NormalizeProviderToken(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeProviderToken(string token) =>
        token.Trim().ToLowerInvariant() switch
        {
            "dml" => "directml",
            "tensorrt-rtx" => "trt-rtx",
            _ => token.Trim().ToLowerInvariant(),
        };
}

public sealed record StarterPackCompatibilityReport(
    string PackId,
    string ProfileId,
    string HardwareProfileKey,
    IReadOnlyList<StageCompatibilityEntry> Stages,
    bool AllStagesRunnable,
    bool AnyFallbackApplied)
{
    public string CompatibilityStatus =>
        !AllStagesRunnable
            ? "not_runnable"
            : AnyFallbackApplied
                ? "fallbacks_required"
                : "fully_compatible";
}

public sealed record StageFallbackRecord(
    string Stage,
    string Alias,
    string RequestedVariant,
    string RequestedExecutionProvider,
    string ResolvedVariant,
    string ResolvedExecutionProvider,
    string? FallbackReason);
