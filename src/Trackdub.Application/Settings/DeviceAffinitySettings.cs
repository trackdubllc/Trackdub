using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Domain;

namespace Trackdub.Application.Settings;

/// <summary>
/// Represents a user-configured device pin for a specific pipeline stage.
/// When present, the runtime planner treats this as an affinity rule override.
/// </summary>
public sealed record DevicePin(
    DeviceKind Kind,
    int DeviceIndex,
    string AdapterDescription);

/// <summary>
/// Persists per-stage device affinity pins to local app data.
/// Pins are identified by adapter description + device index so they survive restarts.
/// Stale pins (referencing devices no longer discovered) are retained in storage
/// but treated as unpinned by the runtime planner until the device reappears.
/// </summary>
public sealed class DeviceAffinitySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string settingsPath;
    private readonly Dictionary<RuntimeStage, DevicePin> pins;

    private DeviceAffinitySettings(
        string settingsPath,
        Dictionary<RuntimeStage, DevicePin> pins,
        bool useOpenVinoCpuProxy,
        bool allowInsecureComponentDownload)
    {
        this.settingsPath = settingsPath;
        this.pins = pins;
        UseOpenVinoCpuProxy = useOpenVinoCpuProxy;
        AllowInsecureComponentDownload = allowInsecureComponentDownload;
    }

    /// <summary>
    /// Gets whether OpenVINO should advertise/use CPU proxy mode instead of NPU mode.
    /// </summary>
    public bool UseOpenVinoCpuProxy { get; }

    /// <summary>
    /// Gets whether OpenVINO component downloads may proceed without hash or size metadata.
    /// </summary>
    public bool AllowInsecureComponentDownload { get; }

    /// <summary>
    /// Loads device affinity settings from the default local app data location.
    /// If the file does not exist or is corrupt, returns empty (all stages set to Auto).
    /// </summary>
    public static DeviceAffinitySettings Load(string? localAppDataRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;

        string settingsPath = Path.Combine(root, "Trackdub", "device-affinity.json");
        LoadedDeviceAffinitySettings loaded = LoadFromDisk(settingsPath);
        return new DeviceAffinitySettings(
            settingsPath,
            loaded.Pins,
            loaded.UseOpenVinoCpuProxy,
            loaded.AllowInsecureComponentDownload);
    }

    /// <summary>
    /// Gets the device pin for the specified stage, or null if the stage is set to "Auto".
    /// </summary>
    public DevicePin? GetPin(RuntimeStage stage) =>
        pins.TryGetValue(stage, out DevicePin? pin) ? pin : null;

    /// <summary>
    /// Returns all current device pins (stages that are not set to Auto).
    /// </summary>
    public IReadOnlyDictionary<RuntimeStage, DevicePin> GetAllPins() =>
        pins.AsReadOnly();

    /// <summary>
    /// Pins a stage to a specific device. Persists the change immediately.
    /// </summary>
    public void PinDevice(RuntimeStage stage, DeviceKind kind, int deviceIndex, string adapterDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterDescription);

        pins[stage] = new DevicePin(kind, deviceIndex, adapterDescription);
        SaveToDisk();
    }

    /// <summary>
    /// Clears the device pin for a stage, returning it to automatic hardware matrix scoring.
    /// Persists the change immediately.
    /// </summary>
    public void ResetToAuto(RuntimeStage stage)
    {
        if (pins.Remove(stage))
        {
            SaveToDisk();
        }
    }

    /// <summary>
    /// Clears all device pins, returning all stages to automatic scoring.
    /// Persists the change immediately.
    /// </summary>
    public void ResetAllToAuto()
    {
        if (pins.Count > 0)
        {
            pins.Clear();
            SaveToDisk();
        }
    }

    private void SaveToDisk()
    {
        string? directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Convert to serializable format keyed by stage name.
        Dictionary<string, DevicePinDto> dtoPins = pins.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => new DevicePinDto(kvp.Value.Kind, kvp.Value.DeviceIndex, kvp.Value.AdapterDescription),
            StringComparer.Ordinal);
        var dto = new DeviceAffinitySettingsDto(
            dtoPins,
            UseOpenVinoCpuProxy,
            AllowInsecureComponentDownload);

        string tempPath = $"{settingsPath}.tmp";
        using (FileStream stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, dto, JsonOptions);
        }

        File.Move(tempPath, settingsPath, overwrite: true);
    }

    private static LoadedDeviceAffinitySettings LoadFromDisk(string path)
    {
        if (!File.Exists(path))
        {
            return LoadedDeviceAffinitySettings.Empty;
        }

        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return LoadedDeviceAffinitySettings.Empty;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return LoadedDeviceAffinitySettings.Empty;
            }

            if (root.TryGetProperty("pins", out _))
            {
                DeviceAffinitySettingsDto? dto = JsonSerializer.Deserialize<DeviceAffinitySettingsDto>(json, JsonOptions);
                Dictionary<RuntimeStage, DevicePin> pins = ConvertPins(dto?.Pins);
                return new LoadedDeviceAffinitySettings(
                    pins,
                    dto?.UseOpenVinoCpuProxy ?? false,
                    dto?.AllowInsecureComponentDownload ?? false);
            }

            // Legacy schema: the root object is a dictionary of pins keyed by stage.
            Dictionary<string, DevicePinDto>? legacyPins = JsonSerializer.Deserialize<Dictionary<string, DevicePinDto>>(json, JsonOptions);
            return new LoadedDeviceAffinitySettings(ConvertPins(legacyPins), false, false);
        }
        catch (JsonException)
        {
            return LoadedDeviceAffinitySettings.Empty;
        }
        catch (IOException)
        {
            return LoadedDeviceAffinitySettings.Empty;
        }
    }

    private static Dictionary<RuntimeStage, DevicePin> ConvertPins(Dictionary<string, DevicePinDto>? dtoPins)
    {
        if (dtoPins is null || dtoPins.Count == 0)
        {
            return [];
        }

        var result = new Dictionary<RuntimeStage, DevicePin>();
        foreach ((string key, DevicePinDto value) in dtoPins)
        {
            if (Enum.TryParse<RuntimeStage>(key, ignoreCase: true, out RuntimeStage stage) &&
                !string.IsNullOrWhiteSpace(value.AdapterDescription))
            {
                result[stage] = new DevicePin(value.Kind, value.DeviceIndex, value.AdapterDescription);
            }
        }

        return result;
    }

    private sealed record LoadedDeviceAffinitySettings(
        Dictionary<RuntimeStage, DevicePin> Pins,
        bool UseOpenVinoCpuProxy,
        bool AllowInsecureComponentDownload)
    {
        public static LoadedDeviceAffinitySettings Empty { get; } = new([], false, false);
    }

    private sealed record DeviceAffinitySettingsDto(
        Dictionary<string, DevicePinDto> Pins,
        bool UseOpenVinoCpuProxy,
        bool AllowInsecureComponentDownload);

    /// <summary>
    /// Internal DTO for JSON serialization of device pins.
    /// </summary>
    private sealed record DevicePinDto(
        DeviceKind Kind,
        int DeviceIndex,
        string AdapterDescription);
}
