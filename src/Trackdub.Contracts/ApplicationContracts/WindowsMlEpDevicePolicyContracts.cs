using System.Linq;

namespace Trackdub.Contracts.ApplicationContracts;

/// <summary>
/// Windows ML ONNX Runtime execution-provider device policy (advanced).
/// <see cref="WindowsMlExecutionDevicePolicy.Explicit"/> keeps explicit catalog device selection (default).
/// Other values use <c>SessionOptions.SetEpSelectionPolicy</c> among registered Windows ML EPs.
/// </summary>
public enum WindowsMlExecutionDevicePolicy
{
    Explicit = 0,
    MaxPerformance = 1,
    PreferNpu = 2,
    MaxEfficiency = 3,
    MinOverallPower = 4,
    DefaultRender = 5,
    MinPower = 6
}

public interface IWindowsMlEpDevicePolicyProvider
{
    Task<WindowsMlExecutionDevicePolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears any in-process policy cache so the next <see cref="GetPolicyAsync"/> reloads settings.
    /// </summary>
    void InvalidateCache();
}

public static class WindowsMlExecutionDevicePolicySettings
{
    public const string ExplicitKey = "explicit";
    public const string MaxPerformanceKey = "max-performance";
    public const string PreferNpuKey = "prefer-npu";
    public const string MaxEfficiencyKey = "max-efficiency";
    public const string MinOverallPowerKey = "min-overall-power";
    public const string DefaultRenderKey = "default-render";
    public const string MinPowerKey = "min-power";

    public static IReadOnlyList<WindowsMlExecutionDevicePolicy> AllPolicies { get; } =
    [
        WindowsMlExecutionDevicePolicy.Explicit,
        WindowsMlExecutionDevicePolicy.MaxPerformance,
        WindowsMlExecutionDevicePolicy.PreferNpu,
        WindowsMlExecutionDevicePolicy.MaxEfficiency,
        WindowsMlExecutionDevicePolicy.MinOverallPower,
        WindowsMlExecutionDevicePolicy.DefaultRender,
        WindowsMlExecutionDevicePolicy.MinPower
    ];

    public static string FormatSupportedKeys(string separator = ", ") =>
        string.Join(separator, AllPolicies.Select(ToKey));

    public static string ToKey(WindowsMlExecutionDevicePolicy policy) =>
        policy switch
        {
            WindowsMlExecutionDevicePolicy.MaxPerformance => MaxPerformanceKey,
            WindowsMlExecutionDevicePolicy.PreferNpu => PreferNpuKey,
            WindowsMlExecutionDevicePolicy.MaxEfficiency => MaxEfficiencyKey,
            WindowsMlExecutionDevicePolicy.MinOverallPower => MinOverallPowerKey,
            WindowsMlExecutionDevicePolicy.DefaultRender => DefaultRenderKey,
            WindowsMlExecutionDevicePolicy.MinPower => MinPowerKey,
            _ => ExplicitKey
        };

    public static bool TryParseKey(string? key, out WindowsMlExecutionDevicePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            policy = WindowsMlExecutionDevicePolicy.Explicit;
            return false;
        }

        return key.Trim().ToLowerInvariant() switch
        {
            ExplicitKey => Assign(WindowsMlExecutionDevicePolicy.Explicit, out policy),
            MaxPerformanceKey => Assign(WindowsMlExecutionDevicePolicy.MaxPerformance, out policy),
            PreferNpuKey => Assign(WindowsMlExecutionDevicePolicy.PreferNpu, out policy),
            MaxEfficiencyKey => Assign(WindowsMlExecutionDevicePolicy.MaxEfficiency, out policy),
            MinOverallPowerKey => Assign(WindowsMlExecutionDevicePolicy.MinOverallPower, out policy),
            DefaultRenderKey => Assign(WindowsMlExecutionDevicePolicy.DefaultRender, out policy),
            MinPowerKey => Assign(WindowsMlExecutionDevicePolicy.MinPower, out policy),
            _ => Assign(WindowsMlExecutionDevicePolicy.Explicit, out policy, success: false)
        };

        static bool Assign(WindowsMlExecutionDevicePolicy value, out WindowsMlExecutionDevicePolicy policy, bool success = true)
        {
            policy = value;
            return success;
        }
    }

    public static WindowsMlExecutionDevicePolicy FromKey(string? key) =>
        TryParseKey(key, out WindowsMlExecutionDevicePolicy policy)
            ? policy
            : WindowsMlExecutionDevicePolicy.Explicit;

    public static string ToDisplayName(WindowsMlExecutionDevicePolicy policy) =>
        policy switch
        {
            WindowsMlExecutionDevicePolicy.MaxPerformance => "Max performance (auto EP/device)",
            WindowsMlExecutionDevicePolicy.PreferNpu => "Prefer NPU",
            WindowsMlExecutionDevicePolicy.MaxEfficiency => "Max efficiency",
            WindowsMlExecutionDevicePolicy.MinOverallPower => "Min overall power",
            WindowsMlExecutionDevicePolicy.DefaultRender => "Default render",
            WindowsMlExecutionDevicePolicy.MinPower => "Min power",
            _ => "Explicit EP selection (default)"
        };
}
