using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

using Trackdub.Cli;

namespace Trackdub.Sdk.Tests;

[Collection(nameof(CliStdoutCaptureCollection))]
public sealed class TrtRtxProvidersCommandTests : IDisposable
{
    private readonly string _emptyModelDirectory = Path.Combine(
        Path.GetTempPath(),
        "TrackdubTests",
        Guid.NewGuid().ToString("N"),
        "models");

    public TrtRtxProvidersCommandTests()
    {
        Directory.CreateDirectory(_emptyModelDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_emptyModelDirectory))
            {
                string? parent = Path.GetDirectoryName(_emptyModelDirectory);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    Directory.Delete(parent, recursive: true);
                }
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task TrtRtxStatusCommand_EmitsJsonWithLicenseFlag()
    {
        using var stdout = new StringWriter();
        int exitCode = await InvokeCliAsync(_emptyModelDirectory, ["providers", "trt-rtx", "status"], stdout);

        Assert.True(exitCode is Program.ExitSuccess or Program.ExitPipelineFailure);

        using JsonDocument document = JsonDocument.Parse(stdout.ToString());
        JsonElement root = document.RootElement;
        Assert.True(root.TryGetProperty("licenseAccepted", out JsonElement licenseAccepted));
        Assert.Equal(JsonValueKind.False, licenseAccepted.ValueKind);
        Assert.True(root.TryGetProperty("blocker", out _));
        Assert.True(root.TryGetProperty("isOrtProviderListed", out _));
    }

    [Fact]
    public async Task TrtRtxInstallCommand_WithoutLicenseAcceptance_ReturnsPipelineFailure()
    {
        using var stdout = new StringWriter();
        int exitCode = await InvokeCliAsync(_emptyModelDirectory, ["providers", "trt-rtx", "install"], stdout);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);

        using JsonDocument document = JsonDocument.Parse(stdout.ToString());
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("succeeded").GetBoolean());
        Assert.Contains("license", root.GetProperty("failureDetail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoctorCommand_IncludesTensorRtRtxPluginCheck()
    {
        using var stdout = new StringWriter();
        int exitCode = await InvokeCliAsync(_emptyModelDirectory, ["doctor"], stdout);

        Assert.Equal(Program.ExitPipelineFailure, exitCode);

        using JsonDocument document = JsonDocument.Parse(stdout.ToString());
        JsonElement checks = document.RootElement.GetProperty("checks");
        bool hasTrtCheck = checks.EnumerateArray()
            .Any(element => element.GetProperty("id").GetString() == "tensorrt-rtx-plugin");
        Assert.True(hasTrtCheck);
    }

    private static async Task<int> InvokeCliAsync(string modelDirectory, string[] args, TextWriter stdout)
    {
        TextWriter originalOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            RootCommand rootCommand = Program.BuildRootCommand(isSetupInteractive: () => false);
            string[] effectiveArgs = ["--model-directory", modelDirectory, .. args];
            ParseResult parseResult = rootCommand.Parse(effectiveArgs);
            return await parseResult.InvokeAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
