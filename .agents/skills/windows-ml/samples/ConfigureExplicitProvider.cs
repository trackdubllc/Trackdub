using System;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;

// Adapted from Microsoft's explicit EP selection example:
// https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers
// Call after the selected EP is registered. This helper performs no installation
// and does not create a session or prove model/provider readiness.
internal static class ConfigureExplicitProvider
{
    public static SessionOptions CreateOptions(
        OrtEnv environment,
        OrtEpDevice selectedDevice,
        string expectedProviderName,
        IReadOnlyDictionary<string, string> providerOptions)
    {
        // The caller obtains this device from this environment's current
        // GetEpDevices result and selects the intended adapter explicitly.
        if (!string.Equals(selectedDevice.EpName, expectedProviderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Selected device belongs to a different execution provider.", nameof(selectedDevice));
        }

        var options = new SessionOptions();
        try
        {
            options.AppendExecutionProvider(environment, new[] { selectedDevice }, providerOptions);
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }
}
