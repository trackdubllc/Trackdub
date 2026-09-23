using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Covers the --execution-provider / --device-policy plumbing: TrackdubBuilder → TrackdubOptions →
/// InMemoryStudioSettingsService, and the CLI global options that feed the builder.
/// </summary>
public sealed class TrackdubExecutionProviderOptionsTests
{
    [Fact]
    public async Task Build_WithExecutionProviderAndDevicePolicy_AppliesToStudioSettings()
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithExecutionProvider(ExecutionProviderPreference.DirectML)
            .WithWindowsMlExecutionDevicePolicy(WindowsMlExecutionDevicePolicy.MaxPerformance)
            .Build();

        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.Equal(WindowsMlExecutionDevicePolicy.MaxPerformance, settings.WindowsMlExecutionDevicePolicy);
        Assert.False(settings.RequirePreferredExecutionProviders);
        Assert.NotEmpty(settings.HardwareOverrides!);
        Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(ExecutionProviderKind.DirectMl, v));
    }

    [Theory]
    [InlineData("trt-rtx", ExecutionProviderKind.TensorRTRtx)]
    [InlineData("qnn", ExecutionProviderKind.Qnn)]
    [InlineData("migraphx", ExecutionProviderKind.Migraphx)]
    [InlineData("vitisai", ExecutionProviderKind.VitisAi)]
    [InlineData("openvino-catalog", ExecutionProviderKind.OpenVinoCatalog)]
    [InlineData("coreml", ExecutionProviderKind.CoreMl)]
    [InlineData("dnnl", ExecutionProviderKind.Dnnl)]
    public async Task TryBuildFactory_VendorTags_MapToHardwareOverridesAsSoftPrefer(string token, ExecutionProviderKind expected)
    {
        using TrackdubSessionFactory factory = CliParseHelpers.TryBuildFactory(
            modelDirectory: null,
            executionProvider: token,
            devicePolicy: null,
            out int exitCode)!;

        Assert.Equal(Program.ExitSuccess, exitCode);
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.False(settings.RequirePreferredExecutionProviders);
        Assert.NotEmpty(settings.HardwareOverrides!);
        Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(expected, v));
    }

    [Fact]
    public async Task TryBuildFactory_RequireExecutionProvider_HardPinsKind()
    {
        using TrackdubSessionFactory factory = CliParseHelpers.TryBuildFactory(
            modelDirectory: null,
            executionProvider: "trt-rtx",
            devicePolicy: null,
            out int exitCode,
            requireExecutionProvider: true)!;

        Assert.Equal(Program.ExitSuccess, exitCode);
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.True(settings.RequirePreferredExecutionProviders);
        Assert.NotEmpty(settings.HardwareOverrides!);
        Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(ExecutionProviderKind.TensorRTRtx, v));
    }

    [Fact]
    public void TryBuildFactory_RequireWithoutKindPin_ReturnsArgumentError()
    {
        TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(
            modelDirectory: null,
            executionProvider: "auto",
            devicePolicy: null,
            out int exitCode,
            requireExecutionProvider: true);

        Assert.Null(factory);
        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public async Task Build_WithExecutionProviderKind_DefaultsToSoftPrefer()
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithExecutionProvider(ExecutionProviderKind.TensorRTRtx)
            .Build();

        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.False(settings.RequirePreferredExecutionProviders);
        Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(ExecutionProviderKind.TensorRTRtx, v));
    }

    [Fact]
    public async Task Build_WithExecutionProviderKindRequireTrue_HardPins()
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithExecutionProvider(ExecutionProviderKind.DirectMl, require: true)
            .Build();

        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.True(settings.RequirePreferredExecutionProviders);
        Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(ExecutionProviderKind.DirectMl, v));
    }

    [Fact]
    public async Task TryBuildFactory_TensorRtTag_MapsToPlatformSoftPrefer()
    {
        using TrackdubSessionFactory factory = CliParseHelpers.TryBuildFactory(
            modelDirectory: null,
            executionProvider: "tensorrt",
            devicePolicy: null,
            out int exitCode)!;

        Assert.Equal(Program.ExitSuccess, exitCode);
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        ExecutionProviderKind expected = OperatingSystem.IsWindows()
            ? ExecutionProviderKind.TensorRTRtx
            : ExecutionProviderKind.TensorRt;
        Assert.False(settings.RequirePreferredExecutionProviders);
        Assert.NotEmpty(settings.HardwareOverrides!);
        Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(expected, v));
    }

    [Fact]
    public async Task Cli_RequireExecutionProviderOption_ThreadsThroughToStudioSettings()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse(
            ["config", "show", "--execution-provider", "trt-rtx", "--require-execution-provider"]);

        TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int exitCode);
        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.NotNull(factory);

        using (factory)
        {
            IStudioSettingsService settingsService = factory!.GetRequiredService<IStudioSettingsService>();
            StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

            Assert.True(settings.RequirePreferredExecutionProviders);
            Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(ExecutionProviderKind.TensorRTRtx, v));
        }
    }

    [Fact]
    public async Task TryBuildFactory_WindowsCudaAlias_MapsToTensorRTRtx()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TrackdubSessionFactory factory = CliParseHelpers.TryBuildFactory(
            modelDirectory: null,
            executionProvider: "cuda",
            devicePolicy: null,
            out int exitCode)!;

        Assert.Equal(Program.ExitSuccess, exitCode);
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(ExecutionProviderKind.TensorRTRtx, v));
    }

    [Fact]
    public void TryParseExecutionProvider_WindowsCuda_EmitsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(CliParseHelpers.TryParseExecutionProvider(
            "cuda",
            out ExecutionProviderKind? kind,
            out string? warning));
        Assert.Equal(ExecutionProviderKind.TensorRTRtx, kind);
        Assert.Contains("trt-rtx", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseExecutionProvider_WindowsTensorRt_EmitsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(CliParseHelpers.TryParseExecutionProvider(
            "tensorrt",
            out ExecutionProviderKind? kind,
            out string? warning));
        Assert.Equal(ExecutionProviderKind.TensorRTRtx, kind);
        Assert.NotNull(warning);
        Assert.Contains("tensorrt", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trt-rtx", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuildFactory_UnknownExecutionProvider_ReturnsArgumentError()
    {
        TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(
            modelDirectory: null,
            executionProvider: "unknown-provider",
            devicePolicy: null,
            out int exitCode);

        Assert.Null(factory);
        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public void TryBuildFactory_UnknownDevicePolicy_ReturnsArgumentError()
    {
        TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(
            modelDirectory: null,
            executionProvider: "auto",
            devicePolicy: "unknown-policy",
            out int exitCode);

        Assert.Null(factory);
        Assert.Equal(Program.ExitArgumentError, exitCode);
    }

    [Fact]
    public async Task Build_DefaultOptions_InheritsHostHardwarePrefs_WhenPresent()
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithLogDirectory(Path.Combine(Path.GetTempPath(), "trackdub-empty-host-" + Guid.NewGuid()))
            .Build();

        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        // Isolated storage: no host settings.json → Explicit policy, no hardware overrides.
        Assert.Equal(WindowsMlExecutionDevicePolicy.Explicit, settings.WindowsMlExecutionDevicePolicy);
        Assert.True(settings.HardwareOverrides is null || settings.HardwareOverrides.Count == 0);
    }

    [Fact]
    public async Task Build_DefaultOptions_MayInheritDiskHardwareOverrides_FromUserSettings()
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder().Build();

        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        // Headless overlay may carry host settings.json hardware pins (e.g. Asr → DirectML).
        // Builder options (null EP / Explicit policy) must not wipe those when present.
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub",
            "settings.json");
        if (!File.Exists(settingsPath) || settings.HardwareOverrides is not { Count: > 0 })
        {
            Assert.Equal(WindowsMlExecutionDevicePolicy.Explicit, settings.WindowsMlExecutionDevicePolicy);
            return;
        }

        Assert.All(settings.HardwareOverrides.Values, v => Assert.NotEqual(ExecutionProviderKind.Cpu, v));
    }

    [Fact]
    public async Task Cli_ExecutionProviderAndDevicePolicyOptions_ThreadThroughToStudioSettings()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse(
            ["config", "show", "--execution-provider", "cuda", "--device-policy", "prefer-npu"]);

        TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int exitCode);
        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.NotNull(factory);

        using (factory)
        {
            IStudioSettingsService settingsService = factory!.GetRequiredService<IStudioSettingsService>();
            StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

            Assert.Equal(WindowsMlExecutionDevicePolicy.PreferNpu, settings.WindowsMlExecutionDevicePolicy);
            Assert.NotEmpty(settings.HardwareOverrides!);
            ExecutionProviderKind expectedEp = OperatingSystem.IsWindows()
                ? ExecutionProviderKind.TensorRTRtx
                : ExecutionProviderKind.Cuda;
            Assert.All(settings.HardwareOverrides!.Values, v => Assert.Equal(expectedEp, v));
        }
    }

    [Fact]
    public void Cli_GlobalOptions_DefaultToAutoAndExplicit()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse(["config", "show"]);

        string? executionProvider = CliParseHelpers.GetGlobalOptionValue<string?>(parseResult, "execution-provider");
        string? devicePolicy = CliParseHelpers.GetGlobalOptionValue<string?>(parseResult, "device-policy");
        bool preferGpu = CliParseHelpers.GetGlobalOptionValue<bool>(parseResult, "prefer-gpu");
        bool requireGpu = CliParseHelpers.GetGlobalOptionValue<bool>(parseResult, "require-gpu");

        Assert.Equal("auto", executionProvider);
        Assert.Equal(WindowsMlExecutionDevicePolicySettings.ExplicitKey, devicePolicy);
        Assert.False(preferGpu);
        Assert.False(requireGpu);
    }

    [Theory]
    [InlineData(false, false, null, false, null, false)]
    [InlineData(true, false, null, false, "vendor-or-dml", false)]
    [InlineData(false, true, null, false, "vendor-or-dml", true)]
    [InlineData(true, false, "directml", false, "directml", false)]
    [InlineData(false, true, "trt-rtx", false, "trt-rtx", true)]
    [InlineData(false, true, "cpu", false, "cpu", true)] // conflict flagged via exit code path separately
    public void CliGpuPreference_Apply_ResolvesVendorThenDml(
        bool preferGpu,
        bool requireGpu,
        string? explicitEp,
        bool explicitRequire,
        string? expectedEpTokenOrSentinel,
        bool expectedRequire)
    {
        (string? ep, bool require, int? error) = CliGpuPreference.Apply(
            explicitEp,
            explicitRequire,
            preferGpu,
            requireGpu);

        if (explicitEp == "cpu" && requireGpu)
        {
            Assert.Equal(Program.ExitArgumentError, error);
            Assert.Equal("cpu", ep);
            return;
        }

        Assert.Null(error);
        Assert.Equal(expectedRequire, require);
        if (expectedEpTokenOrSentinel == "vendor-or-dml")
        {
            Assert.False(string.IsNullOrWhiteSpace(ep));
            Assert.NotEqual("auto", ep);
            Assert.NotEqual("cpu", ep);
            string expected = Trackdub.Domain.ExecutionProviderTokens.ToCanonicalTag(
                CliGpuPreference.ResolvePreferredGpuKind());
            Assert.Equal(expected, ep);
        }
        else if (expectedEpTokenOrSentinel is not null)
        {
            Assert.Equal(expectedEpTokenOrSentinel, ep);
        }
        else
        {
            Assert.Equal(explicitEp, ep);
        }
    }

    [Fact]
    public void CliGpuPreference_ResolvePreferredGpuKind_OnWindowsNvidia_IsVendorThenPlannerFallsThroughToDml()
    {
        ExecutionProviderKind kind = CliGpuPreference.ResolvePreferredGpuKind();

        if (OperatingSystem.IsWindows() && CliGpuPreference.DetectVendor() == CliGpuPreference.GpuVendor.Nvidia)
        {
            Assert.Equal(ExecutionProviderKind.TensorRTRtx, kind);
        }
        else if (OperatingSystem.IsWindows() && CliGpuPreference.DetectVendor() == CliGpuPreference.GpuVendor.Unknown)
        {
            // No vendor detected: still prefer a GPU lane (DirectML) rather than CPU.
            Assert.Equal(ExecutionProviderKind.DirectMl, kind);
        }
        else
        {
            Assert.NotEqual(ExecutionProviderKind.Cpu, kind);
        }
    }

    [Fact]
    public void ResolvePresetExecutionPreferences_PresetUsed_WhenCliNotExplicit()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse(["dub", "--media", "x.mp4", "--target-language", "es"]);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "es",
            ExecutionProvider = "directml",
            DevicePolicy = "max-performance",
        };

        CliParseHelpers.ResolvePresetExecutionPreferences(parseResult, preset, out string? ep, out string? dp);

        Assert.Equal("directml", ep);
        Assert.Equal("max-performance", dp);
    }

    [Fact]
    public void ResolvePresetExecutionPreferences_ExplicitCliWins_OverPreset()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse([
            "dub", "--media", "x.mp4", "--target-language", "es",
            "--execution-provider", "cpu", "--device-policy", "explicit"]);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "es",
            ExecutionProvider = "directml",
            DevicePolicy = "max-performance",
        };

        CliParseHelpers.ResolvePresetExecutionPreferences(parseResult, preset, out string? ep, out string? dp);

        Assert.Equal("cpu", ep);
        Assert.Equal("explicit", dp);
    }

    [Fact]
    public void ResolvePresetExecutionPreferences_EqualsFormCliWins_OverPreset()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse([
            "dub", "--media", "x.mp4", "--target-language", "es",
            "--execution-provider=cpu", "--device-policy:explicit"]);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "es",
            ExecutionProvider = "directml",
            DevicePolicy = "max-performance",
        };

        CliParseHelpers.ResolvePresetExecutionPreferences(parseResult, preset, out string? ep, out string? dp);

        Assert.Equal("cpu", ep);
        Assert.Equal("explicit", dp);
    }

    [Fact]
    public void ResolvePresetExecutionPreferences_Defaults_WhenNoPresetAndNoCli()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse(["dub", "--media", "x.mp4", "--target-language", "es"]);

        CliParseHelpers.ResolvePresetExecutionPreferences(parseResult, null, out string? ep, out string? dp);

        Assert.Null(ep);
        // Device policy falls back to studio settings.json when present; otherwise null/explicit at factory.
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub",
            "settings.json");
        if (File.Exists(settingsPath) && dp is not null)
        {
            Assert.True(WindowsMlExecutionDevicePolicySettings.TryParseKey(dp, out _));
        }
        else
        {
            Assert.True(dp is null || dp == WindowsMlExecutionDevicePolicySettings.ExplicitKey);
        }
    }

    [Fact]
    public async Task Cli_PresetExecutionPreferences_FlowThroughToStudioSettings()
    {
        RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
        ParseResult parseResult = rootCommand.Parse(
            ["dub", "--media", "x.mp4", "--target-language", "es", "--require-execution-provider"]);

        var preset = new PipelinePreset
        {
            Version = 1,
            TargetLanguage = "es",
            ExecutionProvider = "directml",
            DevicePolicy = "max-performance",
        };

        CliParseHelpers.ResolvePresetExecutionPreferences(parseResult, preset, out string? ep, out string? dp);

        using TrackdubSessionFactory presetLoadFactory = CliParseHelpers.TryBuildFactoryForPresetLoad(parseResult, out int presetLoadExitCode)!;
        Assert.Equal(Program.ExitSuccess, presetLoadExitCode);

        using TrackdubSessionFactory factory = CliParseHelpers.TryBuildFactory(
            parseResult,
            null,
            ep,
            dp,
            out int exitCode)!;
        Assert.Equal(Program.ExitSuccess, exitCode);

        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.Equal(WindowsMlExecutionDevicePolicy.MaxPerformance, settings.WindowsMlExecutionDevicePolicy);
        Assert.True(settings.RequirePreferredExecutionProviders);
    }
}
