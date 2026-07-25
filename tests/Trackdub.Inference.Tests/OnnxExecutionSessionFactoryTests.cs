using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.Pool;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using System.Reflection;

namespace Trackdub.Inference.Tests;

public sealed class OnnxExecutionSessionFactoryTests
{
    [Fact]
    public async Task CreatePooledSingleAsync_reuses_pool_hit_without_invoking_session_factory_again()
    {
        using var pool = new InferenceSessionPool(maxSessions: 2);
        int createCount = 0;

        using (await OnnxExecutionSessionFactory.CreatePooledSingleAsync(
            "test-engine",
            "single-model.onnx",
            ExecutionProviderKind.Cpu,
            CancellationToken.None,
            pool,
            sessionFactory: CreateCountingSession))
        {
        }

        using (await OnnxExecutionSessionFactory.CreatePooledSingleAsync(
            "test-engine",
            "single-model.onnx",
            ExecutionProviderKind.Cpu,
            CancellationToken.None,
            pool,
            sessionFactory: CreateCountingSession))
        {
        }

        Assert.Equal(1, createCount);

        InferenceSession CreateCountingSession(string modelPath, SessionOptions options)
        {
            createCount++;
            return CreateMinimalSession();
        }
    }

    [Fact]
    public async Task CreatePooledOpusAsync_reuses_pair_pool_hits_without_invoking_session_factory_again()
    {
        using var pool = new InferenceSessionPool(maxSessions: 4);
        int createCount = 0;

        using (await OnnxExecutionSessionFactory.CreatePooledOpusAsync(
            "test-engine",
            "encoder-model.onnx",
            "decoder-model.onnx",
            ExecutionProviderKind.Cpu,
            CancellationToken.None,
            pool,
            sessionFactory: CreateCountingSession))
        {
        }

        using (await OnnxExecutionSessionFactory.CreatePooledOpusAsync(
            "test-engine",
            "encoder-model.onnx",
            "decoder-model.onnx",
            ExecutionProviderKind.Cpu,
            CancellationToken.None,
            pool,
            sessionFactory: CreateCountingSession))
        {
        }

        Assert.Equal(2, createCount);

        InferenceSession CreateCountingSession(string modelPath, SessionOptions options)
        {
            createCount++;
            return CreateMinimalSession();
        }
    }

    [Fact]
    public async Task CreateSingleAsync_honors_pre_canceled_token_before_directml_session_creation()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OnnxExecutionSessionFactory.CreateSingleAsync(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx"),
                ExecutionProviderKind.DirectMl,
                cancellationSource.Token));
    }

    [Fact]
    public void ResolveSharedSelectedProvider_preserves_directml_when_tensorrt_falls_back_to_gpu_for_both_sessions()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("ResolveSharedSelectedProvider", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate shared provider resolver.");

        object? rawResult = method.Invoke(null,
            [ExecutionProviderKind.TensorRTRtx, ExecutionProviderKind.DirectMl, ExecutionProviderKind.DirectMl]);
        ExecutionProviderKind selectedProvider = Assert.IsType<ExecutionProviderKind>(rawResult);

        Assert.Equal(ExecutionProviderKind.DirectMl, selectedProvider);
    }

    [Fact]
    public void IsTensorRtRtxDeviceCandidate_requires_exact_provider_name_and_gpu_hardware_type()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("IsTensorRtRtxDeviceCandidate", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate TensorRT RTX device filter helper.");

        object? gpuMatch = method.Invoke(null, ["NvTensorRTRTXExecutionProvider", OrtHardwareDeviceType.GPU]);
        object? windowsMlSpellingMatch = method.Invoke(null, ["NvTensorRtRtxExecutionProvider", OrtHardwareDeviceType.GPU]);
        object? legacyNvTrt = method.Invoke(null, ["NvTensorRtExecutionProvider", OrtHardwareDeviceType.GPU]);
        object? legacyTrt = method.Invoke(null, ["TensorRTExecutionProvider", OrtHardwareDeviceType.GPU]);
        object? wrongName = method.Invoke(null, ["RandomOtherProvider", OrtHardwareDeviceType.GPU]);
        object? wrongDeviceType = method.Invoke(null, ["NvTensorRTRTXExecutionProvider", OrtHardwareDeviceType.CPU]);

        Assert.True(Assert.IsType<bool>(gpuMatch));
        Assert.False(Assert.IsType<bool>(windowsMlSpellingMatch));
        Assert.False(Assert.IsType<bool>(legacyNvTrt));
        Assert.False(Assert.IsType<bool>(legacyTrt));
        Assert.False(Assert.IsType<bool>(wrongName));
        Assert.False(Assert.IsType<bool>(wrongDeviceType));
    }

    [Fact]
    public void IsDirectMlDeviceCandidate_requires_directml_provider_name_and_gpu_hardware_type()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("IsDirectMlDeviceCandidate", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate DirectML device filter helper.");

        object? dmlMatch = method.Invoke(null, ["DmlExecutionProvider", OrtHardwareDeviceType.GPU]);
        object? directMlSpellingMatch = method.Invoke(null, ["DirectMLExecutionProvider", OrtHardwareDeviceType.GPU]);
        object? trtRtx = method.Invoke(null, ["NvTensorRtRtxExecutionProvider", OrtHardwareDeviceType.GPU]);
        object? wrongDeviceType = method.Invoke(null, ["DmlExecutionProvider", OrtHardwareDeviceType.CPU]);

        Assert.True(Assert.IsType<bool>(dmlMatch));
        Assert.True(Assert.IsType<bool>(directMlSpellingMatch));
        Assert.False(Assert.IsType<bool>(trtRtx));
        Assert.False(Assert.IsType<bool>(wrongDeviceType));
    }

    [Fact]
    public void BuildTensorRtRtxOptions_includes_runtime_cache_and_cuda_graph_by_default()
    {
        string? previousEngineCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT");
        string? previousCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_CACHE_ROOT");

        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT", null);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", null);

            MethodInfo method = typeof(OnnxExecutionSessionFactory)
                .GetMethod("BuildTensorRtRtxOptions", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not locate TensorRT RTX provider-options helper.");

            object? rawResult = method.Invoke(null, [null]);
            var options = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(rawResult);

            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trackdub", "EngineCache"),
                options["nv_runtime_cache_path"]);
            Assert.Equal("1", options["enable_cuda_graph"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT", previousEngineCacheRoot);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", previousCacheRoot);
        }
    }

    [Fact]
    public void BuildTensorRtRtxOptions_uses_environment_cache_root_by_default()
    {
        string? previousEngineCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT");
        string? previousCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_CACHE_ROOT");
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.Inference.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT", null);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", cacheRoot);

            MethodInfo method = typeof(OnnxExecutionSessionFactory)
                .GetMethod("BuildTensorRtRtxOptions", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not locate TensorRT RTX provider-options helper.");

            object? rawResult = method.Invoke(null, [null]);
            var options = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(rawResult);

            Assert.Equal(Path.Combine(Path.GetFullPath(cacheRoot), "EngineCache"), options["nv_runtime_cache_path"]);
            Assert.Equal("1", options["enable_cuda_graph"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT", previousEngineCacheRoot);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", previousCacheRoot);
        }
    }

    [Fact]
    public void BuildTensorRtRtxOptions_uses_environment_engine_cache_root_by_default()
    {
        string? previousEngineCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT");
        string? previousCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_CACHE_ROOT");
        string engineCacheRoot = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.Inference.Tests",
            Guid.NewGuid().ToString("N"),
            "engine");

        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT", engineCacheRoot);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", Path.Combine(Path.GetTempPath(), "ignored"));

            MethodInfo method = typeof(OnnxExecutionSessionFactory)
                .GetMethod("BuildTensorRtRtxOptions", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not locate TensorRT RTX provider-options helper.");

            object? rawResult = method.Invoke(null, [null]);
            var options = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(rawResult);

            Assert.Equal(Path.GetFullPath(engineCacheRoot), options["nv_runtime_cache_path"]);
            Assert.Equal("1", options["enable_cuda_graph"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT", previousEngineCacheRoot);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", previousCacheRoot);
        }
    }

    [Fact]
    public void BuildTensorRtRtxOptions_allows_callers_to_override_defaults()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("BuildTensorRtRtxOptions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate TensorRT RTX provider-options helper.");

        object? rawResult = method.Invoke(
            null,
            [
                new Dictionary<string, string>
                {
                    ["enable_cuda_graph"] = "0",
                    ["nv_runtime_cache_path"] = @"D:\cache"
                }
            ]);
        var options = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(rawResult);

        Assert.Equal("0", options["enable_cuda_graph"]);
        Assert.Equal(@"D:\cache", options["nv_runtime_cache_path"]);
    }

    [Fact]
    public void BuildSessionOptionsFingerprint_distinguishes_tensorrt_option_overrides()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("BuildSessionOptionsFingerprint", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate session-options fingerprint helper.");

        object? first = method.Invoke(
            null,
            [
                ExecutionProviderKind.TensorRTRtx,
                WindowsMlExecutionDevicePolicy.Explicit,
                new Dictionary<string, string> { ["enable_cuda_graph"] = "1" }
            ]);
        object? second = method.Invoke(
            null,
            [
                ExecutionProviderKind.TensorRTRtx,
                WindowsMlExecutionDevicePolicy.Explicit,
                new Dictionary<string, string> { ["enable_cuda_graph"] = "0" }
            ]);
        object? cpuExplicit = method.Invoke(
            null,
            [ExecutionProviderKind.Cpu, WindowsMlExecutionDevicePolicy.Explicit, null]);
        object? cpuMaxPerf = method.Invoke(
            null,
            [ExecutionProviderKind.Cpu, WindowsMlExecutionDevicePolicy.MaxPerformance, null]);

        Assert.NotEqual(Assert.IsType<string>(first), Assert.IsType<string>(second));
        Assert.Equal(Assert.IsType<string>(cpuExplicit), Assert.IsType<string>(cpuMaxPerf));
    }

    [Fact]
    public void ShouldUseCatalogDevicePolicy_is_false_for_explicit_policy_and_native_cuda()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("ShouldUseCatalogDevicePolicy", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate device policy helper.");

        object? explicitDirectMl = method.Invoke(
            null,
            [WindowsMlExecutionDevicePolicy.Explicit, ExecutionProviderKind.DirectMl]);
        object? policyNativeCuda = method.Invoke(
            null,
            [WindowsMlExecutionDevicePolicy.MaxPerformance, ExecutionProviderKind.Cuda]);

        Assert.False(Assert.IsType<bool>(explicitDirectMl));
        Assert.False(Assert.IsType<bool>(policyNativeCuda));
    }

    [Fact]
    public void BuildSessionOptionsFingerprint_ignores_policy_for_cpu()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("BuildSessionOptionsFingerprint", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate session options fingerprint helper.");

        object? explicitCpu = method.Invoke(
            null,
            [ExecutionProviderKind.Cpu, WindowsMlExecutionDevicePolicy.Explicit, null]);
        object? maxPerfCpu = method.Invoke(
            null,
            [ExecutionProviderKind.Cpu, WindowsMlExecutionDevicePolicy.MaxPerformance, null]);

        Assert.Equal(Assert.IsType<string>(explicitCpu), Assert.IsType<string>(maxPerfCpu));
        Assert.Equal("default", Assert.IsType<string>(explicitCpu));
    }


    [Fact]
    public void BuildSessionOptionsFingerprint_includes_policy_for_catalog_gpu_only()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("BuildSessionOptionsFingerprint", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate session options fingerprint helper.");

        object? explicitDml = method.Invoke(
            null,
            [ExecutionProviderKind.DirectMl, WindowsMlExecutionDevicePolicy.Explicit, null]);
        object? maxPerfDml = method.Invoke(
            null,
            [ExecutionProviderKind.DirectMl, WindowsMlExecutionDevicePolicy.MaxPerformance, null]);
        object? defaultRenderDml = method.Invoke(
            null,
            [ExecutionProviderKind.DirectMl, WindowsMlExecutionDevicePolicy.DefaultRender, null]);
        object? minPowerDml = method.Invoke(
            null,
            [ExecutionProviderKind.DirectMl, WindowsMlExecutionDevicePolicy.MinPower, null]);
        object? maxPerfTrt = method.Invoke(
            null,
            [ExecutionProviderKind.TensorRTRtx, WindowsMlExecutionDevicePolicy.MaxPerformance, null]);
        object? explicitTrt = method.Invoke(
            null,
            [ExecutionProviderKind.TensorRTRtx, WindowsMlExecutionDevicePolicy.Explicit, null]);

        Assert.Equal("default", Assert.IsType<string>(explicitDml));
        Assert.NotEqual(Assert.IsType<string>(explicitDml), Assert.IsType<string>(maxPerfDml));
        Assert.NotEqual(Assert.IsType<string>(maxPerfDml), Assert.IsType<string>(defaultRenderDml));
        Assert.NotEqual(Assert.IsType<string>(maxPerfDml), Assert.IsType<string>(minPowerDml));
        Assert.NotEqual(Assert.IsType<string>(defaultRenderDml), Assert.IsType<string>(minPowerDml));
        Assert.Equal(Assert.IsType<string>(explicitTrt), Assert.IsType<string>(maxPerfTrt));
        Assert.NotEqual(Assert.IsType<string>(maxPerfTrt), Assert.IsType<string>(explicitDml));
    }

    [Theory]
    [InlineData(WindowsMlExecutionDevicePolicy.Explicit, ExecutionProviderKind.DirectMl, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxPerformance, ExecutionProviderKind.DirectMl, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxPerformance, ExecutionProviderKind.TensorRTRtx, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxPerformance, ExecutionProviderKind.Migraphx, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxPerformance, ExecutionProviderKind.Cuda, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxPerformance, ExecutionProviderKind.TensorRt, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.PreferNpu, ExecutionProviderKind.DirectMl, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxEfficiency, ExecutionProviderKind.DirectMl, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MinOverallPower, ExecutionProviderKind.DirectMl, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.DefaultRender, ExecutionProviderKind.DirectMl, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MinPower, ExecutionProviderKind.DirectMl, true)]
    public void ShouldUseCatalogDevicePolicy_matrix(
        WindowsMlExecutionDevicePolicy policy,
        ExecutionProviderKind provider,
        bool expected)
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("ShouldUseCatalogDevicePolicy", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate device policy helper.");

        object? raw = method.Invoke(null, [policy, provider]);
        bool adjustedExpected = expected && OperatingSystem.IsWindows();
        Assert.Equal(adjustedExpected, Assert.IsType<bool>(raw));
    }

    [Theory]
    [InlineData("NvTensorRTRTXExecutionProvider", ExecutionProviderKind.TensorRTRtx)]
    [InlineData("MIGraphXExecutionProvider", ExecutionProviderKind.Migraphx)]
    [InlineData("DnnlExecutionProvider", ExecutionProviderKind.Dnnl)]
    [InlineData("DNNLExecutionProvider", ExecutionProviderKind.Dnnl)]
    [InlineData("DmlExecutionProvider", ExecutionProviderKind.DirectMl)]
    [InlineData("DirectMLExecutionProvider", ExecutionProviderKind.DirectMl)]
    [InlineData("CPUExecutionProvider", ExecutionProviderKind.Cpu)]
    public void TryMapCatalogEpNameToExecutionProviderKind_maps_known_catalog_names(
        string epName,
        ExecutionProviderKind expected)
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("TryMapCatalogEpNameToExecutionProviderKind", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate catalog EP name mapper.");

        object?[] args = [epName, null];
        bool mapped = Assert.IsType<bool>(method.Invoke(null, args));
        Assert.True(mapped);
        Assert.Equal(expected, Assert.IsType<ExecutionProviderKind>(args[1]));
    }

    [Theory]
    [InlineData(WindowsMlExecutionDevicePolicy.DefaultRender, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.MinPower, false)]
    public void BuildDevicePolicyFallbackReason_reports_truthful_fallback_when_extended_policy_not_applied(
        WindowsMlExecutionDevicePolicy devicePolicy,
        bool devicePolicyApplied)
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("BuildDevicePolicyFallbackReason", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate device policy fallback reason helper.");

        object? reason = method.Invoke(null, [devicePolicy, devicePolicyApplied]);

        // The mapper can silently skip SetEpSelectionPolicy when the loaded ORT binding lacks
        // DEFAULT_RENDER/MIN_POWER; this helper must surface that truthfully instead of letting
        // callers assume the requested policy took effect.
        string message = Assert.IsType<string>(reason);
        Assert.Contains("was not applied", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WindowsMlExecutionDevicePolicySettings.ToKey(devicePolicy), message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WindowsMlExecutionDevicePolicy.DefaultRender, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MinPower, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxPerformance, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.PreferNpu, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.Explicit, false)]
    public void BuildDevicePolicyFallbackReason_returns_null_when_policy_applied_or_not_extended(
        WindowsMlExecutionDevicePolicy devicePolicy,
        bool devicePolicyApplied)
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("BuildDevicePolicyFallbackReason", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate device policy fallback reason helper.");

        object? reason = method.Invoke(null, [devicePolicy, devicePolicyApplied]);

        Assert.Null(reason);
    }

    [Theory]
    [InlineData(WindowsMlExecutionDevicePolicy.DefaultRender)]
    [InlineData(WindowsMlExecutionDevicePolicy.MinPower)]
    public void CreateSessionOptions_for_dml_never_claims_extended_policy_active_when_not_applied(
        WindowsMlExecutionDevicePolicy devicePolicy)
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("CreateSessionOptions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate session options factory.");

        object result = method.Invoke(
            null,
            [ExecutionProviderKind.DirectMl, devicePolicy, null])
            ?? throw new InvalidOperationException("Session options factory returned null.");
        Type resultType = result.GetType();
        using var options = Assert.IsAssignableFrom<SessionOptions>(
            resultType.GetProperty("Options")?.GetValue(result));
        PropertyInfo fallbackReasonProp = resultType.GetProperty("FallbackReason")
            ?? throw new InvalidOperationException("SessionOptionsSelection.FallbackReason property not found.");
        string? fallbackReason = fallbackReasonProp.GetValue(result) as string;

        if (!OperatingSystem.IsWindows())
        {
            // ShouldUseCatalogDevicePolicy gates catalog device policies on Windows; off-Windows
            // the extended policy is never requested against ORT. A non-null fallback here is a
            // provider-level CPU fallback (e.g. DirectML unavailable), which must not falsely claim
            // the requested extended device policy was applied.
            if (fallbackReason is not null)
            {
                Assert.DoesNotContain("was applied", fallbackReason, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(WindowsMlExecutionDevicePolicySettings.DefaultRenderKey, fallbackReason, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(WindowsMlExecutionDevicePolicySettings.MinPowerKey, fallbackReason, StringComparison.OrdinalIgnoreCase);
            }

            return;
        }

        // On Windows, either the extended policy was genuinely applied (no fallback reason), or
        // the pinned ORT binding lacks DEFAULT_RENDER/MIN_POWER and the reason must say so —
        // the session must never silently report the extended policy as active when it is not.
        if (fallbackReason is not null)
        {
            Assert.Contains("was not applied", fallbackReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CreateSessionOptions_for_dnnl_records_cpu_fallback_reason_when_append_fails()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("CreateSessionOptions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate session options factory.");

        object result = method.Invoke(
            null,
            [ExecutionProviderKind.Dnnl, WindowsMlExecutionDevicePolicy.Explicit, null])
            ?? throw new InvalidOperationException("Session options factory returned null.");
        Type resultType = result.GetType();
        using var options = Assert.IsAssignableFrom<SessionOptions>(
            resultType.GetProperty("Options")?.GetValue(result));
        var selectedProvider = Assert.IsType<ExecutionProviderKind>(
            resultType.GetProperty("SelectedProvider")?.GetValue(result));
        string? fallbackReason = resultType.GetProperty("FallbackReason")?.GetValue(result) as string;

        if (selectedProvider is ExecutionProviderKind.Dnnl)
        {
            Assert.Null(fallbackReason);
            return;
        }

        Assert.Equal(ExecutionProviderKind.Cpu, selectedProvider);
        Assert.NotNull(fallbackReason);
        Assert.Contains("Requested dnnl", fallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AppendExecutionProvider_Dnnl", fallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryMapCatalogEpNameToExecutionProviderKind_rejects_unknown_names()
    {
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("TryMapCatalogEpNameToExecutionProviderKind", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate catalog EP name mapper.");

        object?[] args = ["TensorrtExecutionProvider", null];
        Assert.False(Assert.IsType<bool>(method.Invoke(null, args)));
    }

    [Fact]
    public void ResolveEffectiveProviderKindFromSession_returns_options_provider_when_catalog_policy_not_used()
    {
        using InferenceSession session = CreateMinimalSession();
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("ResolveEffectiveProviderKindFromSession", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate effective provider resolver.");

        object? raw = method.Invoke(
            null,
            [session, ExecutionProviderKind.DirectMl, false]);
        Assert.Equal(ExecutionProviderKind.DirectMl, Assert.IsType<ExecutionProviderKind>(raw));
    }

    [Fact]
    public void ResolveEffectiveProviderKindFromSession_checks_dnnl_placement_even_without_catalog_policy()
    {
        using InferenceSession session = CreateMinimalSession();
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("ResolveEffectiveProviderKindFromSession", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate effective provider resolver.");

        object? raw = method.Invoke(
            null,
            [session, ExecutionProviderKind.Dnnl, false]);
        Assert.Equal(ExecutionProviderKind.Cpu, Assert.IsType<ExecutionProviderKind>(raw));
    }

    [Fact]
    public void ResolveCatalogEffectiveProviderFromSession_returns_cpu_for_minimal_session()
    {
        using InferenceSession session = CreateMinimalSession();
        MethodInfo method = typeof(OnnxExecutionSessionFactory)
            .GetMethod("ResolveCatalogEffectiveProviderFromSession", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate catalog effective provider resolver.");

        object? raw = method.Invoke(null, [session]);
        Assert.Equal(ExecutionProviderKind.Cpu, Assert.IsType<ExecutionProviderKind>(raw));
    }

    [Fact]
    public void Initialize_parallel_calls_do_not_throw()
    {
        OnnxExecutionProviderBootstrapperRegistry.ResetForTests();
        Parallel.For(
            0,
            8,
            _ => OnnxExecutionProviderBootstrapperRegistry.Initialize(
                new PortableExecutionProviderBootstrapper()));
    }

    [Fact]
    public void ReportUnmappedCatalogEpName_logs_once_per_distinct_ep_name()
    {
        OnnxExecutionProviderBootstrapperRegistry.ResetForTests();
        var logger = new TestLogger();
        OnnxExecutionProviderBootstrapperRegistry.Initialize(
            new PortableExecutionProviderBootstrapper(),
            logger: logger);

        OnnxExecutionSessionFactory.ReportUnmappedCatalogEpName(
            "UnknownEp",
            OnnxExecutionSessionFactory.CatalogEpNameSource.GetEpDeviceForInputs,
            deviceCount: 2);
        OnnxExecutionSessionFactory.ReportUnmappedCatalogEpName(
            "UnknownEp",
            OnnxExecutionSessionFactory.CatalogEpNameSource.GetEpDeviceForInputs,
            deviceCount: 2);
        OnnxExecutionSessionFactory.ReportUnmappedCatalogEpName(
            "AnotherUnknownEp",
            OnnxExecutionSessionFactory.CatalogEpNameSource.GetEpDevices,
            deviceCount: 5);

        Assert.Equal(2, logger.WarningCount);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("UnknownEp", StringComparison.Ordinal));
        Assert.Contains(
            logger.Messages,
            message => message.Contains("AnotherUnknownEp", StringComparison.Ordinal));
    }

    [Fact]
    public void ResetForTests_clears_unmapped_ep_name_dedupe()
    {
        OnnxExecutionProviderBootstrapperRegistry.ResetForTests();
        var logger = new TestLogger();
        OnnxExecutionProviderBootstrapperRegistry.Initialize(
            new PortableExecutionProviderBootstrapper(),
            logger: logger);

        OnnxExecutionSessionFactory.ReportUnmappedCatalogEpName(
            "UnknownEp",
            OnnxExecutionSessionFactory.CatalogEpNameSource.GetEpDeviceForInputs,
            deviceCount: 1);
        OnnxExecutionProviderBootstrapperRegistry.ResetForTests();

        var loggerAfterReset = new TestLogger();
        OnnxExecutionProviderBootstrapperRegistry.Initialize(
            new PortableExecutionProviderBootstrapper(),
            logger: loggerAfterReset);
        OnnxExecutionSessionFactory.ReportUnmappedCatalogEpName(
            "UnknownEp",
            OnnxExecutionSessionFactory.CatalogEpNameSource.GetEpDeviceForInputs,
            deviceCount: 1);

        Assert.Equal(1, logger.WarningCount);
        Assert.Equal(1, loggerAfterReset.WarningCount);
    }

    private sealed class TestLogger : ILogger
    {
        public int WarningCount { get; private set; }

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            WarningCount++;
            Messages.Add(formatter(state, exception));
        }
    }

    private static InferenceSession CreateMinimalSession()
    {
        byte[] model = BuildIdentityOnnxModel();
        return new InferenceSession(model);
    }

    private static byte[] BuildIdentityOnnxModel() =>
    [
        0x08, 0x07, 0x3A, 0x3A, 0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
        0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79, 0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
        0x5A, 0x0F, 0x0A, 0x01, 0x78, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01, 0x12, 0x04, 0x0A,
        0x02, 0x08, 0x01, 0x62, 0x0F, 0x0A, 0x01, 0x79, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01,
        0x12, 0x04, 0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
    ];
}
