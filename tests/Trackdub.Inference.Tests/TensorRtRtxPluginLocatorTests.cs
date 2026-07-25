using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Tests;

public sealed class TensorRtRtxPluginLocatorTests
{
    [Fact]
    public void Resolve_WhenAllDllsExist_ReturnsSuccess()
    {
        string pluginDirectory = SamplePluginDirectory("trt-rtx");
        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: pluginDirectory,
            directoryExists: path => IsSameDirectory(pluginDirectory, path),
            fileExists: path => TensorRtRtxPluginLocator.RequiredFileNames
                .Any(fileName => path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)));

        Assert.True(resolution.Succeeded);
        Assert.Equal(TensorRtRtxPluginDirectorySource.ExplicitStudioSetting, resolution.Source);
        Assert.Equal(
            Path.Combine(NormalizeDirectory(pluginDirectory), TensorRtRtxProviderConstants.PluginLibraryFileName),
            resolution.ProviderLibraryPath);
        Assert.Empty(resolution.MissingFiles);
    }

    [Fact]
    public void Resolve_WhenProviderDllMissing_ReturnsEpNotPresent()
    {
        string pluginDirectory = SamplePluginDirectory("trt-rtx-missing-provider");
        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: pluginDirectory,
            directoryExists: _ => true,
            fileExists: path => !path.EndsWith(TensorRtRtxProviderConstants.PluginLibraryFileName, StringComparison.OrdinalIgnoreCase));

        Assert.False(resolution.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.EpNotPresent, resolution.Blocker);
        Assert.Contains(TensorRtRtxProviderConstants.PluginLibraryFileName, resolution.MissingFiles);
    }

    [Fact]
    public void Resolve_WhenTensorRtRuntimeDllMissing_ReturnsEpNotReady()
    {
        string pluginDirectory = SamplePluginDirectory("trt-rtx-missing-runtime");
        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: pluginDirectory,
            directoryExists: _ => true,
            fileExists: path => !path.EndsWith(TensorRtRtxProviderConstants.TensorRtRuntimeFileName, StringComparison.OrdinalIgnoreCase));

        Assert.False(resolution.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.EpNotReady, resolution.Blocker);
        Assert.Contains(TensorRtRtxProviderConstants.TensorRtRuntimeFileName, resolution.MissingFiles);
    }

    [Fact]
    public void Resolve_WhenDirectoryMissing_ReturnsInvalidDirectory()
    {
        string pluginDirectory = SamplePluginDirectory("missing-trt-rtx");
        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: pluginDirectory,
            directoryExists: _ => false,
            fileExists: _ => false);

        Assert.False(resolution.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.EpNotPresent, resolution.Blocker);
        Assert.Contains("was not found", resolution.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_UsesExplicitSettingBeforeEnvironmentVariable()
    {
        string explicitDirectory = SamplePluginDirectory("explicit-trt-rtx");
        string environmentDirectory = SamplePluginDirectory("env-trt-rtx");

        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: explicitDirectory,
            getEnvironmentVariable: name => name == TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable
                ? environmentDirectory
                : null,
            directoryExists: path => IsSameDirectory(explicitDirectory, path) || IsSameDirectory(environmentDirectory, path),
            fileExists: path => TensorRtRtxPluginLocator.RequiredFileNames
                .Any(fileName => path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)));

        Assert.True(resolution.Succeeded);
        Assert.Equal(TensorRtRtxPluginDirectorySource.ExplicitStudioSetting, resolution.Source);
        Assert.Equal(NormalizeDirectory(explicitDirectory), resolution.DirectoryPath);
    }

    [Fact]
    public void Resolve_UsesEnvironmentVariableBeforeInstalledBundle()
    {
        string environmentDirectory = SamplePluginDirectory("env-trt-rtx");
        string installDirectory = SamplePluginDirectory("installed-trt-rtx");

        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            defaultInstallDirectory: installDirectory,
            getEnvironmentVariable: name => name == TensorRtRtxProviderConstants.PluginDirectoryEnvironmentVariable
                ? environmentDirectory
                : null,
            directoryExists: path => IsSameDirectory(environmentDirectory, path) || IsSameDirectory(installDirectory, path),
            fileExists: path => TensorRtRtxPluginLocator.RequiredFileNames
                .Any(fileName => path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)));

        Assert.True(resolution.Succeeded);
        Assert.Equal(TensorRtRtxPluginDirectorySource.EnvironmentVariable, resolution.Source);
        Assert.Equal(NormalizeDirectory(environmentDirectory), resolution.DirectoryPath);
    }

    [Fact]
    public void Resolve_UsesInstalledBundleWhenConfigured()
    {
        string installDirectory = SamplePluginDirectory("installed-trt-rtx");

        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            defaultInstallDirectory: installDirectory,
            directoryExists: path => IsSameDirectory(installDirectory, path),
            fileExists: path => TensorRtRtxPluginLocator.RequiredFileNames
                .Any(fileName => path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)));

        Assert.True(resolution.Succeeded);
        Assert.Equal(TensorRtRtxPluginDirectorySource.InstalledBundle, resolution.Source);
        Assert.Equal(NormalizeDirectory(installDirectory), resolution.DirectoryPath);
    }

    [Fact]
    public void LinuxBundleFileNames_UseSharedObjectSuffixes()
    {
        Assert.Equal("libonnxruntime_providers_nv_tensorrt_rtx.so", TensorRtRtxProviderConstants.PluginLibraryFileNameLinux);
        Assert.Equal("libtensorrt_rtx.so", TensorRtRtxProviderConstants.TensorRtRuntimeFileNameLinux);
        Assert.Equal("libtensorrt_onnxparser_rtx.so", TensorRtRtxProviderConstants.TensorRtOnnxParserFileNameLinux);
    }

    [Fact]
    public void Resolve_WhenExplicitBundlePresent_UsesPlatformFileNames()
    {
        string pluginDirectory = SamplePluginDirectory("trt-rtx-platform");

        TensorRtRtxPluginResolution resolution = TensorRtRtxPluginLocator.Resolve(
            explicitPluginDirectory: pluginDirectory,
            directoryExists: path => IsSameDirectory(pluginDirectory, path),
            fileExists: path => TensorRtRtxPluginLocator.RequiredFileNames
                .Any(fileName => path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)));

        Assert.True(resolution.Succeeded);
        Assert.Equal(
            Path.Combine(NormalizeDirectory(pluginDirectory), TensorRtRtxProviderConstants.PluginLibraryFileName),
            resolution.ProviderLibraryPath);
    }

    private static string SamplePluginDirectory(string leaf) =>
        OperatingSystem.IsLinux()
            ? Path.Combine(Path.GetTempPath(), "trackdub", leaf)
            : Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", leaf);

    private static string NormalizeDirectory(string directory) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));

    private static bool IsSameDirectory(string expected, string actual) =>
        string.Equals(NormalizeDirectory(expected), actual, StringComparison.Ordinal);
}
