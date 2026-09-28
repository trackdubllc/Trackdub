using System.Runtime.CompilerServices;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Pool;

internal sealed class CpuExecutionAdmission
{
    private const string MaxConcurrencyEnvironmentVariable = "TRACKDUB_CPU_INFERENCE_MAX_CONCURRENCY";

    private static readonly Lazy<CpuExecutionAdmission> shared =
        new(() => new CpuExecutionAdmission(ParseSharedLimit()));

    private readonly SemaphoreSlim gate;
    private readonly ConditionalWeakTable<InferenceSession, ProviderRegistration> registrations = new();
    private readonly object registryLock = new();

    internal CpuExecutionAdmission(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);

        MaxConcurrency = maxConcurrency;
        gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    internal static CpuExecutionAdmission Shared => shared.Value;

    internal int MaxConcurrency { get; }

    internal void RegisterSession(InferenceSession session, ExecutionProviderKind provider)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (registryLock)
        {
            registrations.Remove(session);
            registrations.Add(session, new ProviderRegistration(provider));
        }
    }

    internal IDisposable? Acquire(
        InferenceSession session,
        ExecutionProviderKind? explicitProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Acquire(ResolveProvider(session, explicitProvider), cancellationToken);
    }

    internal async ValueTask<IDisposable?> AcquireAsync(
        InferenceSession session,
        ExecutionProviderKind? explicitProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await AcquireAsync(ResolveProvider(session, explicitProvider), cancellationToken)
            .ConfigureAwait(false);
    }

    internal IDisposable? Acquire(ExecutionProviderKind? provider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RequiresPermit(provider))
        {
            return null;
        }

        if (gate.Wait(0, cancellationToken))
        {
            BenchmarkPhaseCapture.Increment("cpuExecutionAdmissionAcquire");
            return new Releaser(gate);
        }

        BenchmarkPhaseCapture.Increment("cpuExecutionAdmissionWait");
        using (BenchmarkPhaseCapture.Start("cpu-execution-admission-wait"))
        {
            gate.Wait(cancellationToken);
        }

        BenchmarkPhaseCapture.Increment("cpuExecutionAdmissionAcquire");
        return new Releaser(gate);
    }

    internal async ValueTask<IDisposable?> AcquireAsync(
        ExecutionProviderKind? provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RequiresPermit(provider))
        {
            return null;
        }

        if (gate.Wait(0, cancellationToken))
        {
            BenchmarkPhaseCapture.Increment("cpuExecutionAdmissionAcquire");
            return new Releaser(gate);
        }

        BenchmarkPhaseCapture.Increment("cpuExecutionAdmissionWait");
        using (BenchmarkPhaseCapture.Start("cpu-execution-admission-wait"))
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        BenchmarkPhaseCapture.Increment("cpuExecutionAdmissionAcquire");
        return new Releaser(gate);
    }

    private static int ParseSharedLimit() =>
        int.TryParse(
            Environment.GetEnvironmentVariable(MaxConcurrencyEnvironmentVariable),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed) && parsed is >= 1 and <= 1024
            ? parsed
            : 1;

    private ExecutionProviderKind? ResolveProvider(InferenceSession session, ExecutionProviderKind? explicitProvider) =>
        registrations.TryGetValue(session, out ProviderRegistration? registration)
            ? registration.Provider
            : explicitProvider;

    private static bool RequiresPermit(ExecutionProviderKind? provider) =>
        provider is null or ExecutionProviderKind.Cpu or ExecutionProviderKind.Dnnl;

    private sealed class ProviderRegistration(ExecutionProviderKind provider)
    {
        public ExecutionProviderKind Provider { get; } = provider;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? gate = gate;

        public void Dispose() => Interlocked.Exchange(ref gate, null)?.Release();
    }
}
