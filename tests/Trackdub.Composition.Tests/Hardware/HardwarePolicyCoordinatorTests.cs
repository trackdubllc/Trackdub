using Trackdub.Composition.Hardware;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Composition.Tests.Hardware;

public sealed class HardwarePolicyCoordinatorTests
{
    [Fact]
    public async Task ApplyAndEvictAsync_invalidates_policy_cache_and_evicts_idle_sessions()
    {
        var policyProvider = new TestWindowsMlEpDevicePolicyProvider();
        var evictor = new TestInferenceSessionPoolEvictor();
        var logger = new TestApplicationLogger();
        var coordinator = new HardwarePolicyCoordinator(policyProvider, evictor, logger);

        bool result = await coordinator.ApplyAndEvictAsync(TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(1, policyProvider.InvalidateCount);
        Assert.Equal(1, evictor.EvictCount);
        Assert.Null(logger.WarningMessage);
    }

    [Fact]
    public async Task ApplyAndEvictAsync_logs_warning_and_returns_false_when_eviction_fails()
    {
        var policyProvider = new TestWindowsMlEpDevicePolicyProvider();
        var evictor = new TestInferenceSessionPoolEvictor
        {
            ExceptionToThrow = new InvalidOperationException("pool locked")
        };
        var logger = new TestApplicationLogger();
        var coordinator = new HardwarePolicyCoordinator(policyProvider, evictor, logger);

        bool result = await coordinator.ApplyAndEvictAsync(TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(1, policyProvider.InvalidateCount);
        Assert.Equal(1, evictor.EvictCount);
        Assert.Equal("Failed to evict idle ONNX sessions after hardware policy change.", logger.WarningMessage);
        Assert.Same(evictor.ExceptionToThrow, logger.WarningException);
    }

    private sealed class TestWindowsMlEpDevicePolicyProvider : IWindowsMlEpDevicePolicyProvider
    {
        public int InvalidateCount { get; private set; }

        public Task<WindowsMlExecutionDevicePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowsMlExecutionDevicePolicy.Explicit);

        public void InvalidateCache() => InvalidateCount++;
    }

    private sealed class TestInferenceSessionPoolEvictor : IInferenceSessionPoolEvictor
    {
        public int EvictCount { get; private set; }

        public Exception? ExceptionToThrow { get; init; }

        public Task EvictAllIdleAsync(CancellationToken cancellationToken = default)
        {
            EvictCount++;
            return ExceptionToThrow is null ? Task.CompletedTask : Task.FromException(ExceptionToThrow);
        }
    }

    private sealed class TestApplicationLogger : IApplicationLogger
    {
        public string? WarningMessage { get; private set; }

        public Exception? WarningException { get; private set; }

        public void LogDebug(string message) { }

        public void LogInformation(string message) { }

        public void LogWarning(string message, Exception? exception = null)
        {
            WarningMessage = message;
            WarningException = exception;
        }

        public void LogError(string message, Exception? exception = null) { }
    }
}
