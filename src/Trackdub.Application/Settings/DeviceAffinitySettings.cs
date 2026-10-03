using System.Text.Json;

namespace Trackdub.Application.Settings;

/// <summary>
/// Reads OpenVINO device affinity options from local app data.
/// </summary>
/// <remarks>
/// The settings file is hand-edited: no UI or CLI writes it. Missing, malformed, or unrecognized
/// content falls back to defaults rather than failing startup, so a file left over from an older
/// schema is harmless.
/// </remarks>
public sealed class DeviceAffinitySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private DeviceAffinitySettings(bool useOpenVinoCpuProxy)
    {
        UseOpenVinoCpuProxy = useOpenVinoCpuProxy;
    }

    /// <summary>
    /// Gets whether OpenVINO should advertise/use CPU proxy mode instead of NPU mode.
    /// </summary>
    public bool UseOpenVinoCpuProxy { get; }

    /// <summary>
    /// Loads device affinity settings from the default local app data location.
    /// </summary>
    public static DeviceAffinitySettings Load(string? localAppDataRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;

        string settingsPath = Path.Join(root, "Trackdub", "device-affinity.json");
        return new DeviceAffinitySettings(LoadFromDisk(settingsPath));
    }

    private static bool LoadFromDisk(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            DeviceAffinitySettingsDto? dto = JsonSerializer.Deserialize<DeviceAffinitySettingsDto>(json, JsonOptions);
            return dto?.UseOpenVinoCpuProxy ?? false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed record DeviceAffinitySettingsDto(bool UseOpenVinoCpuProxy);
}
