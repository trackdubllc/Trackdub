using Trackdub.Domain;

namespace Trackdub.Application.ModelOptimization;

public sealed class OlivePreflightValidator
{
    public OlivePreflightResult Validate(
        ModelOptimizationAvailability availability,
        OliveExecutionProvider executionProvider,
        string precision)
    {
        string normalizedPrecision = NormalizePrecision(precision);
        ExecutionProviderKind providerKind = MapProvider(executionProvider);

        if (!availability.HasProfile || !availability.CanOptimize)
        {
            return OlivePreflightResult.Fail(availability.UnavailableReason ?? "Optimization profile is unavailable.");
        }

        if (!availability.AvailableProviders.Contains(providerKind))
        {
            return OlivePreflightResult.Fail(
                $"Provider '{executionProvider}' is not available for this model on this machine.");
        }

        IReadOnlyList<string> allowedPrecisions = ResolveAllowedPrecisions(availability, executionProvider);
        if (!allowedPrecisions.Contains(normalizedPrecision, StringComparer.OrdinalIgnoreCase))
        {
            return OlivePreflightResult.Fail(
                $"Precision '{normalizedPrecision}' is not supported for provider '{executionProvider}'.");
        }

        ModelOptimizationOpsetPolicy? policy = MatchOpsetPolicy(availability, providerKind, normalizedPrecision);
        if (policy is null)
        {
            return OlivePreflightResult.Pass();
        }

        if (availability.DeclaredOpset is int declaredOpset)
        {
            if (declaredOpset < policy.MinimumOpset)
            {
                return OlivePreflightResult.Fail(
                    $"Model opset {declaredOpset} is below required opset {policy.MinimumOpset} for provider '{executionProvider}' and precision '{normalizedPrecision}'.");
            }

            return OlivePreflightResult.Pass();
        }

        if (availability.RequireOpsetMetadata)
        {
            return OlivePreflightResult.Fail(
                $"Opset metadata is required for this optimization profile, but no model opset was declared.");
        }

        return OlivePreflightResult.Pass(
            $"No opset metadata declared; skipped opset policy check for '{executionProvider}' + '{normalizedPrecision}'.");
    }

    public IReadOnlyList<string> ResolveAllowedPrecisions(
        ModelOptimizationAvailability availability,
        OliveExecutionProvider executionProvider)
    {
        string[] defaults = executionProvider switch
        {
            OliveExecutionProvider.Cpu => ["fp32", "int8"],
            OliveExecutionProvider.Dml or OliveExecutionProvider.Cuda => ["fp16", "int8", "int4"],
            OliveExecutionProvider.TensorRt or OliveExecutionProvider.TensorRtRtx => ["fp16"],
            OliveExecutionProvider.Migraphx or OliveExecutionProvider.Rocm => ["fp16", "int8"],
            OliveExecutionProvider.Qnn or OliveExecutionProvider.OpenVino or OliveExecutionProvider.VitisAi => ["int8"],
            _ => ["fp32"]
        };

        if (availability.SupportedPrecisions.Count == 0)
        {
            return defaults;
        }

        return defaults
            .Where(defaultPrecision => availability.SupportedPrecisions.Contains(defaultPrecision, StringComparer.OrdinalIgnoreCase))
            .Select(NormalizePrecision)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ModelOptimizationOpsetPolicy? MatchOpsetPolicy(
        ModelOptimizationAvailability availability,
        ExecutionProviderKind provider,
        string normalizedPrecision)
    {
        return availability.OpsetPolicies
            .Where(policy => policy.Provider is null || policy.Provider == provider)
            .Where(policy => policy.Precision is null || policy.Precision.Equals(normalizedPrecision, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(policy => policy.Provider is not null)
            .ThenByDescending(policy => policy.Precision is not null)
            .FirstOrDefault();
    }

    private static string NormalizePrecision(string precision) =>
        string.IsNullOrWhiteSpace(precision)
            ? "fp32"
            : precision.Trim().ToLowerInvariant();

    private static ExecutionProviderKind MapProvider(OliveExecutionProvider provider) =>
        provider switch
        {
            OliveExecutionProvider.Dml => ExecutionProviderKind.DirectMl,
            OliveExecutionProvider.Cuda => ExecutionProviderKind.Cuda,
            OliveExecutionProvider.TensorRt => ExecutionProviderKind.TensorRt,
            OliveExecutionProvider.TensorRtRtx => ExecutionProviderKind.TensorRTRtx,
            OliveExecutionProvider.Migraphx or OliveExecutionProvider.Rocm => ExecutionProviderKind.Migraphx,
            OliveExecutionProvider.Qnn => ExecutionProviderKind.Qnn,
            OliveExecutionProvider.OpenVino => ExecutionProviderKind.OpenVinoCatalog,
            OliveExecutionProvider.VitisAi => ExecutionProviderKind.VitisAi,
            _ => ExecutionProviderKind.Cpu
        };
}

public sealed record OlivePreflightResult(
    bool IsAllowed,
    string? ErrorReason = null,
    string? Warning = null)
{
    public static OlivePreflightResult Pass(string? warning = null) => new(true, null, warning);

    public static OlivePreflightResult Fail(string reason) => new(false, reason, null);
}
