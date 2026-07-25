#if WINDOWS
using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.WindowsMl;

internal static class WindowsMlExecutionDevicePolicyMapper
{
    // Capability probes for ExecutionProviderDevicePolicy.DEFAULT_RENDER / MIN_POWER.
    // The currently pinned Microsoft.ML.OnnxRuntime 1.24.4 managed enum surface omits these
    // members. Package/runtime version numbers do not reliably predict when they appear, so
    // probe the enum once via TryParse and let DefaultRender / MinPower silently no-op when
    // unavailable instead of throwing inside Map. The probes are independent: DefaultRender only
    // needs DEFAULT_RENDER and MinPower only needs MIN_POWER; a combined AND check would block
    // one policy when the other enum member is missing.
    private static readonly Lazy<bool> OrtHasDefaultRender = new(static () =>
        Enum.TryParse("DEFAULT_RENDER", ignoreCase: false, out ExecutionProviderDevicePolicy _));
    private static readonly Lazy<bool> OrtHasMinPower = new(static () =>
        Enum.TryParse("MIN_POWER", ignoreCase: false, out ExecutionProviderDevicePolicy _));

    /// <returns>
    /// <c>true</c> when <see cref="SessionOptions.SetEpSelectionPolicy"/> was actually invoked
    /// for <paramref name="policy"/>; <c>false</c> when the call was skipped (either because
    /// <paramref name="policy"/> is <see cref="WindowsMlExecutionDevicePolicy.Explicit"/>, or the
    /// requested extended policy could not be honored because the managed ORT enum surface lacks
    /// the required members). Callers must not assume the requested policy took effect just
    /// because this method was called with a non-<c>Explicit</c> policy — check the return value
    /// and surface a fallback reason when it is <c>false</c>, so telemetry/fingerprints never
    /// silently report a policy as active when it was not applied to the session.
    /// </returns>
    internal static bool ApplyIfNeeded(SessionOptions options, WindowsMlExecutionDevicePolicy policy)
    {
        if (policy == WindowsMlExecutionDevicePolicy.Explicit)
        {
            return false;
        }

        // Extended policies need their own DEFAULT_RENDER / MIN_POWER enum member on the managed
        // ORT surface; when absent, preserve Explicit-style semantics (no SetEpSelectionPolicy call)
        // and report that the policy was NOT applied so callers can surface a truthful fallback
        // reason instead of silently claiming the extended policy took effect. Probes are split
        // per-policy so a missing DEFAULT_RENDER does not block MinPower and vice versa.
        if ((policy is WindowsMlExecutionDevicePolicy.DefaultRender && !OrtHasDefaultRender.Value) ||
            (policy is WindowsMlExecutionDevicePolicy.MinPower && !OrtHasMinPower.Value))
        {
            return false;
        }

        options.SetEpSelectionPolicy(Map(policy));
        return true;
    }

    /// <summary>
    /// Single owner: <see cref="ApplyIfNeeded"/> is the entry point used on the public path. Map is
    /// private so no caller can bypass the <see cref="OrtHasDefaultRender"/> /
    /// <see cref="OrtHasMinPower"/> gate for
    /// <see cref="WindowsMlExecutionDevicePolicy.DefaultRender"/> / <see cref="WindowsMlExecutionDevicePolicy.MinPower"/>;
    /// <see cref="ResolveByName"/> still throws as a defensive guard against reflective misuse.
    /// </summary>
    private static ExecutionProviderDevicePolicy Map(WindowsMlExecutionDevicePolicy policy) =>
        policy switch
        {
            WindowsMlExecutionDevicePolicy.MaxPerformance => ExecutionProviderDevicePolicy.MAX_PERFORMANCE,
            WindowsMlExecutionDevicePolicy.PreferNpu => ExecutionProviderDevicePolicy.PREFER_NPU,
            WindowsMlExecutionDevicePolicy.MaxEfficiency => ExecutionProviderDevicePolicy.MAX_EFFICIENCY,
            WindowsMlExecutionDevicePolicy.MinOverallPower => ExecutionProviderDevicePolicy.MIN_OVERALL_POWER,
            WindowsMlExecutionDevicePolicy.DefaultRender => ResolveByName("DEFAULT_RENDER"),
            WindowsMlExecutionDevicePolicy.MinPower => ResolveByName("MIN_POWER"),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unsupported Windows ML device policy.")
        };

    private static ExecutionProviderDevicePolicy ResolveByName(string name) =>
        Enum.TryParse<ExecutionProviderDevicePolicy>(name, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(
                nameof(name),
                name,
                $"ORT managed binding lacks ExecutionProviderDevicePolicy.{name}.");
}
#endif
