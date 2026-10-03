using Trackdub.Application.Settings;

namespace Trackdub.Application.Tests;

public sealed class DeviceAffinitySettingsTests
{
    [Fact]
    public void Load_WhenSettingsFileIsMissing_DefaultsOpenVinoCpuProxyToFalse()
    {
        string rootPath = CreateRootPath();

        try
        {
            DeviceAffinitySettings settings = DeviceAffinitySettings.Load(rootPath);

            Assert.False(settings.UseOpenVinoCpuProxy);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public void Load_WhenCpuProxyIsEnabled_ReadsIt()
    {
        string rootPath = CreateRootPath();

        try
        {
            WriteSettings(rootPath, """
            {
              "useOpenVinoCpuProxy": true
            }
            """);

            DeviceAffinitySettings settings = DeviceAffinitySettings.Load(rootPath);

            Assert.True(settings.UseOpenVinoCpuProxy);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    /// <summary>
    /// User files written by the older schema carry device pins and an insecure-download flag that
    /// no longer exist. They must still load, ignoring the dropped keys, so upgrading does not
    /// reset a user's CPU-proxy choice or fail startup.
    /// </summary>
    [Fact]
    public void Load_WhenOlderSchemaCarriesDroppedKeys_IgnoresThemAndKeepsCpuProxy()
    {
        string rootPath = CreateRootPath();

        try
        {
            WriteSettings(rootPath, """
            {
              "pins": {
                "vad": {
                  "kind": "cpu",
                  "deviceIndex": 0,
                  "adapterDescription": "CPU"
                }
              },
              "useOpenVinoCpuProxy": true,
              "allowInsecureComponentDownload": true
            }
            """);

            DeviceAffinitySettings settings = DeviceAffinitySettings.Load(rootPath);

            Assert.True(settings.UseOpenVinoCpuProxy);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public void Load_WhenLegacyPinOnlySchemaIsPresent_DefaultsCpuProxyToFalse()
    {
        string rootPath = CreateRootPath();

        try
        {
            WriteSettings(rootPath, """
            {
              "vad": {
                "kind": "cpu",
                "deviceIndex": 0,
                "adapterDescription": "CPU"
              }
            }
            """);

            DeviceAffinitySettings settings = DeviceAffinitySettings.Load(rootPath);

            Assert.False(settings.UseOpenVinoCpuProxy);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    [Fact]
    public void Load_WhenFileIsCorrupt_DefaultsCpuProxyToFalse()
    {
        string rootPath = CreateRootPath();

        try
        {
            WriteSettings(rootPath, "{ not json");

            DeviceAffinitySettings settings = DeviceAffinitySettings.Load(rootPath);

            Assert.False(settings.UseOpenVinoCpuProxy);
        }
        finally
        {
            DeleteRootPath(rootPath);
        }
    }

    private static string CreateRootPath() =>
        Path.Join(Path.GetTempPath(), "Trackdub.DeviceAffinitySettings.Tests", Guid.NewGuid().ToString("N"));

    private static void WriteSettings(string rootPath, string json)
    {
        string settingsPath = Path.Join(rootPath, "Trackdub", "device-affinity.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, json);
    }

    private static void DeleteRootPath(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
