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
    public async Task TrtRtxSmokeCommand_EmitsSmokeReportJson()
    {
        using var stdout = new StringWriter();
        int exitCode = await InvokeCliAsync(_emptyModelDirectory, ["providers", "trt-rtx", "smoke"], stdout);

        // The command runs on both plugin-ready (RTX) and plugin-not-ready machines,
        // so accept either the all-pass success code or the pipeline-failure code.
        Assert.True(exitCode is Program.ExitSuccess or Program.ExitPipelineFailure);

        using JsonDocument document = JsonDocument.Parse(stdout.ToString());
        JsonElement root = document.RootElement;

        // 'ready' must always be emitted as a JSON boolean.
        Assert.True(root.TryGetProperty("ready", out JsonElement ready));
        Assert.True(ready.ValueKind is JsonValueKind.True or JsonValueKind.False);

        // 'attempted' must always be emitted as a non-negative integer.
        Assert.True(root.TryGetProperty("attempted", out JsonElement attempted));
        Assert.Equal(JsonValueKind.Number, attempted.ValueKind);
        Assert.True(attempted.GetInt32() >= 0);

        if (ready.GetBoolean())
        {
            // Ready branch: with zero attempts (no starter-pack models cached), the
            // command always takes the no-attempts path and fails the pipeline. When
            // there are attempts, the exit code depends on which cached models pass,
            // which is environment-dependent, so it is left unconstrained here.
            if (attempted.GetInt32() == 0)
            {
                Assert.Equal(Program.ExitPipelineFailure, exitCode);
            }
        }
        else
        {
            // Not-ready branch: fails the pipeline, reports zero attempts, and names a blocker.
            Assert.Equal(Program.ExitPipelineFailure, exitCode);
            Assert.Equal(0, attempted.GetInt32());
            Assert.True(root.TryGetProperty("blocker", out _));
        }
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

        bool hasEspeakCheck = checks.EnumerateArray()
            .Any(element => element.GetProperty("id").GetString() == "espeak-ng");
        Assert.True(hasEspeakCheck);
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
