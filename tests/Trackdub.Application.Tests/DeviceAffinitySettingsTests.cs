using Trackdub.Application.Settings;
using Trackdub.Domain;

namespace Trackdub.Application.Tests;

public sealed class DeviceAffinitySettingsTests
{
    [Fact]
    public void Load_WhenSettingsFileIsMissing_DefaultsOpenVinoFlagsToFalse()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "Trackdub.DeviceAffinitySettings.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            DeviceAffinitySettings settings = DeviceAffinitySettings.Load(rootPath);

            Assert.False(settings.UseOpenVinoCpuProxy);
            Assert.False(settings.AllowInsecureComponentDownload);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_WhenNewSchemaIsPresent_ReadsOpenVinoFlags()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "Trackdub.DeviceAffinitySettings.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            string settingsPath = Path.Combine(rootPath, "Trackdub", "device-affinity.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, """
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
            Assert.True(settings.AllowInsecureComponentDownload);
            Assert.Equal(DeviceKind.Cpu, settings.GetPin(RuntimeStage.Vad)!.Kind);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_WhenLegacySchemaIsPresent_DefaultsOpenVinoFlagsToFalse()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "Trackdub.DeviceAffinitySettings.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            string settingsPath = Path.Combine(rootPath, "Trackdub", "device-affinity.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, """
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
            Assert.False(settings.AllowInsecureComponentDownload);
            Assert.Equal(DeviceKind.Cpu, settings.GetPin(RuntimeStage.Vad)!.Kind);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
