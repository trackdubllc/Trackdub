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
    public async Task Build_DefaultOptions_UseExplicitPolicyAndNoHardwareOverrides()
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder().Build();

        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.Equal(WindowsMlExecutionDevicePolicy.Explicit, settings.WindowsMlExecutionDevicePolicy);
        Assert.Empty(settings.HardwareOverrides!);
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

        Assert.Equal("auto", executionProvider);
        Assert.Equal(WindowsMlExecutionDevicePolicySettings.ExplicitKey, devicePolicy);
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
        Assert.Null(dp);
    }

    [Fact]
    public async Task Cli_PresetExecutionPreferences_FlowThroughToStudioSettings()
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

        using TrackdubSessionFactory factory = CliParseHelpers.TryBuildFactory(null, ep, dp, out int exitCode)!;
        Assert.Equal(Program.ExitSuccess, exitCode);

        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(CancellationToken.None);

        Assert.Equal(WindowsMlExecutionDevicePolicy.MaxPerformance, settings.WindowsMlExecutionDevicePolicy);
    }
}
