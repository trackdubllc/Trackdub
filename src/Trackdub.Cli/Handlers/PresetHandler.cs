using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

internal static class PresetHandler
{
    public static async Task<int> SaveAsync(
        string name,
        PipelinePreset preset,
        PresetStore store,
        TextWriter output,
        CancellationToken ct)
    {
        if (!PresetNameValidator.IsValid(name))
        {
            await output.WriteLineAsync(
                $"Invalid preset name '{name}'. Names must be 1-64 characters using only alphanumeric, hyphens, and underscores.")
                .ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        if (!TryValidateExecutionPreferences(preset, out string? validationError))
        {
            await output.WriteLineAsync(validationError!).ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        try
        {
            await store.SaveAsync(name, preset, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await output.WriteLineAsync($"Failed to save preset '{name}': {ex.Message}").ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        string filePath = Path.Combine(store.PresetsDirectory, $"{name}.json");
        await output.WriteLineAsync($"Saved preset '{name}' to {filePath}").ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    public static async Task<int> LoadAsync(
        string name,
        PresetStore store,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (!PresetNameValidator.IsValid(name))
        {
            await error.WriteLineAsync(
                $"Invalid preset name '{name}'. Names must be 1-64 characters using only alphanumeric, hyphens, and underscores.")
                .ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        PipelinePreset? preset;
        try
        {
            preset = await store.LoadAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            string filePath = Path.Combine(store.PresetsDirectory, $"{name}.json");
            await error.WriteLineAsync($"Failed to read preset '{name}' at {filePath}: {ex.Message}").ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        if (preset is null)
        {
            await error.WriteLineAsync($"Preset '{name}' not found.").ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        await output.WriteLineAsync($"target-language: {preset.TargetLanguage}").ConfigureAwait(false);

        if (preset.SourceLanguage is not null)
        {
            await output.WriteLineAsync($"source-language: {preset.SourceLanguage}").ConfigureAwait(false);
        }

        if (preset.Models is { Count: > 0 })
        {
            string modelsValue = string.Join(", ", preset.Models.Select(kv => $"{kv.Key}={kv.Value}"));
            await output.WriteLineAsync($"models: {modelsValue}").ConfigureAwait(false);
        }

        if (preset.ExportFormat is not null)
        {
            await output.WriteLineAsync($"export-format: {preset.ExportFormat}").ConfigureAwait(false);
        }

        if (preset.ExecutionProvider is not null)
        {
            await output.WriteLineAsync($"execution-provider: {preset.ExecutionProvider}").ConfigureAwait(false);
        }

        if (preset.DevicePolicy is not null)
        {
            await output.WriteLineAsync($"device-policy: {preset.DevicePolicy}").ConfigureAwait(false);
        }

        if (preset.EnableAsrTextRefinement is not null)
        {
            await output.WriteLineAsync($"enable-asr-text-refinement: {preset.EnableAsrTextRefinement.Value.ToString().ToLowerInvariant()}")
                .ConfigureAwait(false);
        }

        return Program.ExitSuccess;
    }

    public static async Task<int> ListAsync(
        PresetStore store,
        TextWriter output,
        CancellationToken ct)
    {
        IReadOnlyList<string> names = await store.ListAsync(ct).ConfigureAwait(false);

        if (names.Count == 0)
        {
            await output.WriteLineAsync("No presets saved.").ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        foreach (string name in names)
        {
            await output.WriteLineAsync(name).ConfigureAwait(false);
        }

        return Program.ExitSuccess;
    }

    public static async Task<int> DeleteAsync(
        string name,
        PresetStore store,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (!PresetNameValidator.IsValid(name))
        {
            await error.WriteLineAsync(
                $"Invalid preset name '{name}'. Names must be 1-64 characters using only alphanumeric, hyphens, and underscores.")
                .ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        bool deleted;
        try
        {
            deleted = await store.DeleteAsync(name, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await error.WriteLineAsync($"Failed to delete preset '{name}': {ex.Message}").ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        if (!deleted)
        {
            await error.WriteLineAsync($"Preset '{name}' not found.").ConfigureAwait(false);
            return Program.ExitArgumentError;
        }

        await output.WriteLineAsync($"Deleted preset '{name}'.").ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// Validates the <see cref="PipelinePreset.ExecutionProvider"/> and
    /// <see cref="PipelinePreset.DevicePolicy"/> fields using the same parsers that back
    /// <c>CliParseHelpers.TryBuildFactory</c>. Empty values are allowed (they mean "use the
    /// application default"); only non-empty values that fail to parse are rejected.
    /// </summary>
    internal static bool TryValidateExecutionPreferences(
        PipelinePreset preset,
        out string? errorMessage)
    {
        if (preset.ExecutionProvider is { Length: > 0 }
            && !CliParseHelpers.TryParseExecutionProvider(preset.ExecutionProvider, out _))
        {
            errorMessage =
                $"Invalid execution provider: '{preset.ExecutionProvider}'. Expected one of: {CliParseHelpers.FormatSupportedExecutionProviders()}.";
            return false;
        }

        if (preset.DevicePolicy is { Length: > 0 }
            && !CliParseHelpers.TryParseDevicePolicy(preset.DevicePolicy, out _))
        {
            errorMessage =
                $"Invalid device policy: '{preset.DevicePolicy}'. Expected one of: {WindowsMlExecutionDevicePolicySettings.FormatSupportedKeys()}.";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
