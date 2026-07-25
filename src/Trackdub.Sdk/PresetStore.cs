using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Sdk;

/// <summary>
/// Persists and retrieves named pipeline presets from the user data root.
/// </summary>
public sealed class PresetStore
{
    private readonly string _presetsDirectory;

    /// <summary>
    /// Gets the directory path where presets are stored.
    /// </summary>
    public string PresetsDirectory => _presetsDirectory;

    internal static readonly JsonSerializerOptions PresetJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public PresetStore(string presetsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetsDirectory);
        _presetsDirectory = presetsDirectory;
    }

    /// <summary>
    /// Save a preset. Overwrites if name exists. Uses atomic write (temp file + rename).
    /// Creates the presets directory if missing.
    /// </summary>
    public async Task SaveAsync(string name, PipelinePreset preset, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(preset);

        if (preset.Version > PipelinePreset.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Cannot save preset with version {preset.Version}. Maximum supported version is {PipelinePreset.CurrentVersion}.");
        }

        Directory.CreateDirectory(_presetsDirectory);

        string targetPath = GetPresetPath(name);
        string tempPath = Path.Combine(_presetsDirectory, $"{name}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });

                // We serialize manually to avoid BOM and ensure UTF-8 no BOM via Utf8JsonWriter.
                JsonSerializer.Serialize(writer, preset, PresetJsonOptions);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            // Clean up temp file if rename failed
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Load a preset by name. Returns null if not found.
    /// Tolerant of unrecognized fields (they are ignored by default STJ behavior).
    /// </summary>
    public async Task<PipelinePreset?> LoadAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string filePath = GetPresetPath(name);

        if (!File.Exists(filePath))
        {
            return null;
        }

        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        PipelinePreset? preset = await JsonSerializer.DeserializeAsync<PipelinePreset>(stream, ReadOptions, ct);

        if (preset is not null && preset.Version > PipelinePreset.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Preset '{name}' has unsupported schema version {preset.Version}. " +
                $"This version of Trackdub supports preset schema version {PipelinePreset.CurrentVersion} or earlier.");
        }

        return preset;
    }

    /// <summary>
    /// List all valid preset names, sorted alphabetically (OrdinalIgnoreCase).
    /// Skips malformed JSON files. Returns empty list if directory does not exist.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_presetsDirectory))
        {
            return [];
        }

        string[] files = Directory.GetFiles(_presetsDirectory, "*.json");
        List<string> names = [];

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                PipelinePreset? preset = await JsonSerializer.DeserializeAsync<PipelinePreset>(stream, ReadOptions, ct);

                if (preset is not null)
                {
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (JsonException)
            {
                // Skip malformed files
            }
            catch (IOException)
            {
                // Skip files that can't be read
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Delete a preset by name. Returns true if deleted, false if not found.
    /// Uses platform-aware name matching: OrdinalIgnoreCase on Windows, Ordinal on Linux/macOS.
    /// </summary>
    public Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string filePath = GetPresetPath(name);

        if (!File.Exists(filePath))
        {
            // On case-sensitive file systems, the exact path must match.
            // On Windows, File.Exists already handles case-insensitivity at the OS level.
            return Task.FromResult(false);
        }

        // On case-sensitive platforms, verify the file name matches exactly.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string expectedFileName = $"{name}.json";
            string? directory = Path.GetDirectoryName(filePath);

            if (directory is not null && Directory.Exists(directory))
            {
                string[] files = Directory.GetFiles(directory, "*.json");
                bool found = files.Any(f =>
                    string.Equals(Path.GetFileName(f), expectedFileName, StringComparison.Ordinal));

                if (!found)
                {
                    return Task.FromResult(false);
                }
            }
        }

        File.Delete(filePath);
        return Task.FromResult(true);
    }

    private string GetPresetPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!PresetNameValidator.IsValid(name))
        {
            throw new ArgumentException(
                "Preset name must be 1-64 characters using only alphanumeric characters, hyphens, and underscores.",
                nameof(name));
        }

        if (Path.IsPathRooted(name) ||
            name.Contains('/') ||
            name.Contains('\\') ||
            name.Contains(".."))
        {
            throw new ArgumentException(
                "Preset name must not contain path separators or traversal patterns.",
                nameof(name));
        }

        return Path.Combine(_presetsDirectory, $"{name}.json");
    }
}
