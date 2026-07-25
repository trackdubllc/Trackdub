using System.Runtime.CompilerServices;
using Trackdub.Contracts;
using Trackdub.Application.ModelOptimization;
using Trackdub.Domain;
using Trackdub.Infrastructure.ModelOptimization;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class OliveModelOptimizationServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(),
        "Trackdub.OliveModelOptimizationService.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OptimizeAsync_uses_only_declared_components()
    {
        string modelRoot = Path.Combine(tempRoot, "model");
        string outputRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        WriteFile(Path.Combine(modelRoot, "top-level.onnx"), "top");
        WriteFile(Path.Combine(modelRoot, "nested", "declared.onnx"), "nested");
        var runner = new FakeProcessRunner(createModelOutput: true);
        var registrar = new FakeVariantRegistrar();
        var service = CreateService(runner, registrar);

        await DrainAsync(service.OptimizeAsync(
            new ModelOptimizationRequest(
                "example/model",
                modelRoot,
                outputRoot,
                OliveExecutionProvider.Cpu,
                "fp32",
                ["nested/declared.onnx"],
                "olive-cpu-fp32",
                "nested/declared.onnx"),
            TestContext.Current.CancellationToken));

        Assert.Single(runner.Calls);
        Assert.Contains(Path.Combine(modelRoot, "nested", "declared.onnx"), runner.Calls[0].Arguments);
        Assert.DoesNotContain(Path.Combine(modelRoot, "top-level.onnx"), runner.Calls[0].Arguments);
        Assert.True(File.Exists(Path.Combine(outputRoot, "nested", "declared.onnx")));
        Assert.False(File.Exists(Path.Combine(outputRoot, "top-level.onnx")));
        ModelOptimizedVariantRegistration registration = Assert.Single(registrar.Registrations);
        Assert.Equal("olive-cpu-fp32", registration.VariantAlias);
        Assert.Equal("nested/declared.onnx", registration.EntryRelativePath);
    }

    [Theory]
    [InlineData(OliveExecutionProvider.Cpu, "CPUExecutionProvider", "cpu", "fp32", ExecutionProviderKind.Cpu)]
    [InlineData(OliveExecutionProvider.Dml, "DmlExecutionProvider", "gpu", "fp16", ExecutionProviderKind.DirectMl)]
    [InlineData(OliveExecutionProvider.Cuda, "CUDAExecutionProvider", "gpu", "fp16", ExecutionProviderKind.Cuda)]
    [InlineData(OliveExecutionProvider.TensorRt, "TensorrtExecutionProvider", "gpu", "fp16", ExecutionProviderKind.TensorRt)]
    [InlineData(OliveExecutionProvider.TensorRtRtx, "NvTensorRTRTXExecutionProvider", "gpu", "fp16", ExecutionProviderKind.TensorRTRtx)]
    public async Task OptimizeAsync_maps_provider_to_olive_arguments_and_registration(
        OliveExecutionProvider oliveProvider,
        string expectedProvider,
        string expectedDevice,
        string precision,
        ExecutionProviderKind expectedExecutionProvider)
    {
        string modelRoot = Path.Combine(tempRoot, $"provider-{oliveProvider}");
        string alias = $"olive-{oliveProvider.ToString().ToLowerInvariant()}-{precision}";
        string outputRoot = Path.Combine(modelRoot, "optimized", alias);
        WriteFile(Path.Combine(modelRoot, "model.onnx"), "source");
        var runner = new FakeProcessRunner(createModelOutput: true);
        var registrar = new FakeVariantRegistrar();
        var service = CreateService(runner, registrar);

        await DrainAsync(service.OptimizeAsync(
            new ModelOptimizationRequest(
                "example/model",
                modelRoot,
                outputRoot,
                oliveProvider,
                precision,
                ["model.onnx"],
                alias,
                "model.onnx"),
            TestContext.Current.CancellationToken));

        ProcessCall call = Assert.Single(runner.Calls);
        Assert.Equal("optimize", call.Arguments[0]);
        Assert.Contains("--provider", call.Arguments);
        Assert.Equal(expectedProvider, ArgumentAfter(call, "--provider"));
        Assert.Contains("--device", call.Arguments);
        Assert.Equal(expectedDevice, ArgumentAfter(call, "--device"));
        Assert.Contains("--precision", call.Arguments);
        Assert.Equal(precision, ArgumentAfter(call, "--precision"));
        ModelOptimizedVariantRegistration registration = Assert.Single(registrar.Registrations);
        Assert.Equal(expectedExecutionProvider, registration.ExecutionProvider);
    }

    [Theory]
    [InlineData("../model.onnx")]
    [InlineData("nested/../model.onnx")]
    public async Task OptimizeAsync_rejects_unsafe_component_paths(string componentPath)
    {
        string modelRoot = Path.Combine(tempRoot, "unsafe-model");
        Directory.CreateDirectory(modelRoot);
        var service = CreateService(new FakeProcessRunner(createModelOutput: true), new FakeVariantRegistrar());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DrainAsync(service.OptimizeAsync(
                new ModelOptimizationRequest(
                    "example/model",
                    modelRoot,
                    Path.Combine(modelRoot, "optimized", "cpu-fp32"),
                    OliveExecutionProvider.Cpu,
                    "fp32",
                    [componentPath],
                    "olive-cpu-fp32",
                    componentPath),
                TestContext.Current.CancellationToken)));

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OptimizeAsync_preserves_previous_output_and_cleans_temp_when_run_fails()
    {
        string modelRoot = Path.Combine(tempRoot, "failing-model");
        string outputRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        WriteFile(Path.Combine(modelRoot, "model.onnx"), "source");
        WriteFile(Path.Combine(outputRoot, "model.onnx"), "previous");
        var runner = new FakeProcessRunner(createModelOutput: false);
        var registrar = new FakeVariantRegistrar();
        var service = CreateService(runner, registrar);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DrainAsync(service.OptimizeAsync(
                new ModelOptimizationRequest(
                    "example/model",
                    modelRoot,
                    outputRoot,
                    OliveExecutionProvider.Cpu,
                    "fp32",
                    ["model.onnx"],
                    "olive-cpu-fp32",
                    "model.onnx"),
                TestContext.Current.CancellationToken)));

        Assert.Contains("did not produce", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(outputRoot, "model.onnx"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(outputRoot)!, "*.tmp-*"));
        Assert.Empty(registrar.Registrations);
    }

    [Fact]
    public async Task OptimizeAsync_restores_previous_output_when_registration_fails()
    {
        string modelRoot = Path.Combine(tempRoot, "registration-failure-model");
        string outputRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        WriteFile(Path.Combine(modelRoot, "model.onnx"), "source");
        WriteFile(Path.Combine(outputRoot, "model.onnx"), "previous");
        var runner = new FakeProcessRunner(createModelOutput: true);
        var registrar = new FakeVariantRegistrar(new InvalidOperationException("registration failed"));
        var service = CreateService(runner, registrar);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DrainAsync(service.OptimizeAsync(
                new ModelOptimizationRequest(
                    "example/model",
                    modelRoot,
                    outputRoot,
                    OliveExecutionProvider.Cpu,
                    "fp32",
                    ["model.onnx"],
                    "olive-cpu-fp32",
                    "model.onnx"),
                TestContext.Current.CancellationToken)));

        Assert.Contains("registration failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(outputRoot, "model.onnx"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(outputRoot)!, "*.tmp-*"));
    }

    [Fact]
    public async Task OptimizeAsync_genai_bundle_optimizes_each_top_level_onnx_separately()
    {
        string modelRoot = Path.Combine(tempRoot, "whisper-genai");
        string outputRoot = Path.Combine(modelRoot, "optimized", "olive-dml-fp16");
        WriteFile(Path.Combine(modelRoot, "encoder.onnx"), "encoder");
        WriteFile(Path.Combine(modelRoot, "decoder.onnx"), "decoder");
        WriteFile(Path.Combine(modelRoot, "genai_config.json"), "{}");
        WriteFile(Path.Combine(modelRoot, "audio_processor_config.json"), "{}");
        WriteFile(Path.Combine(modelRoot, "tokenizer.json"), "{}");

        var runner = new FakeProcessRunner(createModelOutput: true);
        var registrar = new FakeVariantRegistrar();
        var service = CreateService(runner, registrar);

        await DrainAsync(service.OptimizeAsync(
            new ModelOptimizationRequest(
                "openai/whisper-tiny",
                modelRoot,
                outputRoot,
                OliveExecutionProvider.Dml,
                "fp16",
                ["encoder.onnx", "decoder.onnx"],
                "olive-dml-fp16",
                "encoder.onnx",
                OliveMode: "ort-genai-builder"),
            TestContext.Current.CancellationToken));

        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Contains(".onnx", ArgumentAfter(call, "--model_name_or_path"), StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(outputRoot, "encoder.onnx")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "decoder.onnx")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "genai_config.json")));
        ModelOptimizedVariantRegistration registration = Assert.Single(registrar.Registrations);
        Assert.Equal("genai_config.json", registration.EntryRelativePath);
        Assert.Contains("encoder.onnx", registration.ComponentRelativePaths);
        Assert.Contains("decoder.onnx", registration.ComponentRelativePaths);
    }

    [Fact]
    public async Task OptimizeAsync_genai_bundle_with_shared_component_cache_still_optimizes_each_onnx_separately()
    {
        // Regression: the no-recipe ort-genai-builder default sets UseSharedComponentCache=true, which
        // previously routed multi-onnx bundles through whole-folder model-builder auto-opt and crashed
        // ("Found multiple .onnx model files. Please specify one."). Multi-onnx must stay per-component.
        string modelRoot = Path.Combine(tempRoot, "whisper-genai-shared");
        string outputRoot = Path.Combine(modelRoot, "optimized", "olive-tensorrtrtx-fp16");
        WriteFile(Path.Combine(modelRoot, "encoder.onnx"), "encoder");
        WriteFile(Path.Combine(modelRoot, "decoder.onnx"), "decoder");
        WriteFile(Path.Combine(modelRoot, "genai_config.json"), "{}");
        WriteFile(Path.Combine(modelRoot, "tokenizer.json"), "{}");

        var runner = new FakeProcessRunner(createModelOutput: true);
        var registrar = new FakeVariantRegistrar();
        var service = CreateService(runner, registrar);

        await DrainAsync(service.OptimizeAsync(
            new ModelOptimizationRequest(
                "openai/whisper-tiny",
                modelRoot,
                outputRoot,
                OliveExecutionProvider.TensorRtRtx,
                "fp16",
                ["encoder.onnx", "decoder.onnx"],
                "olive-tensorrtrtx-fp16",
                "encoder.onnx",
                OliveMode: "ort-genai-builder",
                UseSharedComponentCache: true),
            TestContext.Current.CancellationToken));

        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Contains(".onnx", ArgumentAfter(call, "--model_name_or_path"), StringComparison.OrdinalIgnoreCase));
        Assert.All(runner.Calls, call => Assert.Equal("optimize", call.Arguments[0]));
        Assert.All(runner.Calls, call => Assert.DoesNotContain("--exporter", call.Arguments));
        Assert.True(File.Exists(Path.Combine(outputRoot, "encoder.onnx")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "decoder.onnx")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "genai_config.json")));
    }

    [Fact]
    public async Task OptimizeAsync_genai_bundle_removes_ephemeral_olive_work_directories()
    {
        string modelRoot = Path.Combine(tempRoot, "whisper-genai-cleanup");
        string outputRoot = Path.Combine(modelRoot, "optimized", "olive-cpu-fp32");
        WriteFile(Path.Combine(modelRoot, "encoder.onnx"), "encoder");
        WriteFile(Path.Combine(modelRoot, "decoder.onnx"), "decoder");
        WriteFile(Path.Combine(modelRoot, "genai_config.json"), "{}");

        var runner = new FakeProcessRunner(createModelOutput: true);
        var registrar = new FakeVariantRegistrar();
        var service = CreateService(runner, registrar);

        await DrainAsync(service.OptimizeAsync(
            new ModelOptimizationRequest(
                "openai/whisper-tiny",
                modelRoot,
                outputRoot,
                OliveExecutionProvider.Cpu,
                "fp32",
                ["encoder.onnx", "decoder.onnx"],
                "olive-cpu-fp32",
                "encoder.onnx",
                OliveMode: "ort-genai-builder"),
            TestContext.Current.CancellationToken));

        string cacheRoot = Path.Combine(tempRoot, "tools", "olive-cache", "openai_whisper-tiny");
        if (Directory.Exists(cacheRoot))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(cacheRoot));
        }
    }

    [Fact]
    public async Task OptimizeAsync_genai_nested_bundle_uses_entry_subfolder()
    {
        string modelRoot = Path.Combine(tempRoot, "phi-genai");
        string bundleFolder = Path.Combine(modelRoot, "cpu_and_mobile", "cpu-int4-rtn-block-32-acc-level-4");
        string outputRoot = Path.Combine(modelRoot, "optimized", "olive-dml-fp16");
        WriteFile(Path.Combine(bundleFolder, "genai_config.json"), "{}");
        WriteFile(Path.Combine(bundleFolder, "model.onnx"), "model");
        WriteFile(Path.Combine(bundleFolder, "tokenizer.json"), "{}");

        var runner = new FakeProcessRunner(createModelOutput: true, createGenAiConfigOutput: true);
        var registrar = new FakeVariantRegistrar();
        var service = CreateService(runner, registrar);

        await DrainAsync(service.OptimizeAsync(
            new ModelOptimizationRequest(
                "microsoft/Phi-4-mini-instruct-onnx",
                modelRoot,
                outputRoot,
                OliveExecutionProvider.Dml,
                "fp16",
                ["genai_config.json"],
                "olive-dml-fp16",
                "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/genai_config.json",
                OliveMode: "ort-genai-builder"),
            TestContext.Current.CancellationToken));

        ProcessCall call = Assert.Single(runner.Calls);
        Assert.Equal("optimize", call.Arguments[0]);
        Assert.Equal(bundleFolder, ArgumentAfter(call, "--model_name_or_path"));
        Assert.Equal("model_builder", ArgumentAfter(call, "--exporter"));
        Assert.True(File.Exists(Path.Combine(outputRoot, "genai_config.json")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "model.onnx")));
        ModelOptimizedVariantRegistration registration = Assert.Single(registrar.Registrations);
        Assert.Equal("genai_config.json", registration.EntryRelativePath);
    }

    [Fact]
    public async Task OptimizeAsync_uses_recipe_run_config_when_override_set()
    {
        string modelRoot = Path.Combine(tempRoot, "recipe-model");
        string outputRoot = Path.Combine(modelRoot, "optimized", "olive-dml-int8");
        string recipeConfig = Path.Combine(tempRoot, "recipe.json");
        WriteFile(recipeConfig, "{}");
        WriteFile(Path.Combine(modelRoot, "encoder.onnx"), "source");
        var runner = new FakeProcessRunner(createModelOutput: true);
        var registrar = new FakeVariantRegistrar();
        var service = CreateService(runner, registrar);

        List<string> lines = [];
        await foreach (string line in service.OptimizeAsync(
            new ModelOptimizationRequest(
                "openai/whisper-tiny",
                modelRoot,
                outputRoot,
                OliveExecutionProvider.Dml,
                "int8",
                ["encoder.onnx"],
                "olive-dml-int8",
                "encoder.onnx",
                OliveRecipeConfigPath: recipeConfig),
            TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        ProcessCall call = Assert.Single(runner.Calls);
        Assert.Equal("run", call.Arguments[0]);
        Assert.Equal("--config", call.Arguments[1]);
        Assert.Equal(recipeConfig, call.Arguments[2]);
        Assert.Contains(lines, line => line.Contains("[progress] Step 1/3", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private OliveModelOptimizationService CreateService(
        FakeProcessRunner runner,
        FakeVariantRegistrar registrar)
    {
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        return new OliveModelOptimizationService(
            new FakeOliveEnvironmentService(),
            storagePaths,
            runner,
            registrar);
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static async Task DrainAsync(IAsyncEnumerable<string> lines)
    {
        await foreach (string _ in lines.ConfigureAwait(false))
        {
            // Intentionally empty: consume all lines to completion.
        }
    }

    private static string ArgumentAfter(ProcessCall call, string option)
    {
        int index = Array.IndexOf(call.Arguments.ToArray(), option);
        Assert.True(index >= 0 && index + 1 < call.Arguments.Count, $"Missing argument value after {option}.");
        return call.Arguments[index + 1];
    }

    private sealed class FakeProcessRunner(bool createModelOutput, bool createGenAiConfigOutput = false) : IStreamingProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public async IAsyncEnumerable<string> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add(new ProcessCall(executable, arguments.ToArray(), workingDirectory));
            if (createModelOutput)
            {
                WriteFile(Path.Combine(workingDirectory, "model.onnx"), "optimized");
            }

            if (createGenAiConfigOutput)
            {
                WriteFile(Path.Combine(workingDirectory, "genai_config.json"), "{}");
            }

            await Task.CompletedTask;
            yield return "Step 1/3: warmup";
            yield return "done";
        }
    }

    private sealed record ProcessCall(
        string Executable,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory);

    private sealed class FakeOliveEnvironmentService : IOliveEnvironmentService
    {
        public string GetManagedPythonPath(OliveExecutionProvider provider) => "python";

        public string GetOliveExecutablePath(OliveExecutionProvider provider) => "olive";

        public Task<OliveEnvironmentStatus> GetStatusAsync(
            OliveExecutionProvider provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OliveEnvironmentStatus(true, "3.11", true, true));

        public async IAsyncEnumerable<string> BootstrapAsync(
            OliveExecutionProvider provider,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeVariantRegistrar(Exception? failure = null) : IModelVariantRegistrar
    {
        public List<ModelOptimizedVariantRegistration> Registrations { get; } = [];

        public Task RegisterAsync(
            ModelOptimizedVariantRegistration registration,
            CancellationToken cancellationToken = default)
        {
            if (failure is not null)
            {
                throw failure;
            }

            Registrations.Add(registration);
            return Task.CompletedTask;
        }
    }
}
