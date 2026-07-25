// Feature: hardware-matrix-routing, Property 12: Failed Session Not Cached

using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Property-based tests verifying that when the session factory throws an exception
/// during session creation, the InferenceSessionPool does NOT cache a failed entry
/// for that key, and the exception propagates to the caller unmodified.
///
/// **Validates: Requirements 3.4, 3.5**
/// </summary>
public sealed class FailedSessionNotCachedPropertyTests
{
    private static readonly ExecutionProviderKind[] AllProviders =
    [
        ExecutionProviderKind.Cpu,
        ExecutionProviderKind.DirectMl,
        ExecutionProviderKind.TensorRTRtx,
        ExecutionProviderKind.OpenVino
    ];

    /// <summary>
    /// Input record for the property test, holding the parameters needed to construct
    /// a SessionPoolKey and a failure message for the simulated exception.
    /// </summary>
    public sealed record FailedSessionInput(
        string EngineFamily,
        string? ModelId,
        ExecutionProviderKind Provider,
        int PathSuffix,
        int? DeviceId,
        string GraphRole,
        string ExceptionMessage);

    private static Arbitrary<FailedSessionInput> FailedSessionInputArb()
    {
        var gen = from engineFamily in Gen.Elements("kokoro", "whisper-onnx", "opus-mt", "silero-vad", "chatterbox")
                  from modelId in Gen.Elements<string?>("model-a", "model-b", null)
                  from provider in Gen.Elements(AllProviders)
                  from pathSuffix in Gen.Choose(1, 1000)
                  from deviceId in Gen.Elements<int?>(null, 0, 1, 2, 3)
                  from graphRole in Gen.Elements("default", "encoder", "decoder")
                  from exceptionMessage in Gen.Elements(
                      "OOM simulation",
                      "[ErrorCode:RuntimeException] Out of memory",
                      "Device lost",
                      "Provider initialization failed",
                      "Model load error")
                  select new FailedSessionInput(
                      engineFamily, modelId, provider, pathSuffix, deviceId, graphRole, exceptionMessage);

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 12: When the session factory throws an exception, the pool has no cached
    /// entry for that key after the exception propagates, and the exception reaches the
    /// caller unmodified.
    ///
    /// Test strategy:
    /// 1. Create an InferenceSessionPool
    /// 2. Construct a SessionPoolKey from generated inputs
    /// 3. Call GetLeaseAsync with a factory that throws InvalidOperationException
    /// 4. Verify the exception propagates with the exact same instance (unmodified)
    /// 5. Call GetLeaseAsync again with a working factory — verify it invokes the factory
    ///    (proving nothing was cached from the failed attempt)
    ///
    /// **Validates: Requirements 3.4, 3.5**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = [typeof(FailedSessionNotCachedPropertyTests)])]
    public bool FailedFactory_DoesNotCache_AndPropagatesException(FailedSessionInput input)
    {
        using var pool = new InferenceSessionPool(maxSessions: 4);

        var pathHash = SessionPoolKey.HashPath($@"C:\Models\model_{input.PathSuffix}.onnx");
        var key = new SessionPoolKey(
            input.EngineFamily, input.ModelId, null,
            input.Provider, pathHash, input.DeviceId, input.GraphRole);

        var expectedException = new InvalidOperationException(input.ExceptionMessage);

        // Step 1: Call GetLeaseAsync with a factory that throws — exception must propagate.
        InvalidOperationException? caught = null;
        try
        {
            pool.GetLeaseAsync(
                key,
                _ => throw expectedException,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        if (caught is null)
            return false; // Exception did not propagate

        // Verify the exception is the exact same instance (unmodified).
        if (!ReferenceEquals(caught, expectedException))
            return false;

        // Step 2: Call GetLeaseAsync again with a working factory.
        // If the pool cached a failed entry, the factory would NOT be called.
        int factoryCalls = 0;
        using (pool.GetLeaseAsync(
            key,
            _ =>
            {
                factoryCalls++;
                return Task.FromResult(CreateMinimalSession());
            },
            CancellationToken.None).GetAwaiter().GetResult())
        {
        }

        // Factory must have been called exactly once, proving no cached entry existed.
        return factoryCalls == 1;
    }

    /// <summary>
    /// Creates a minimal ONNX identity model in-memory so tests can obtain a real
    /// <see cref="InferenceSession"/> without loading any file from disk.
    /// </summary>
    private static InferenceSession CreateMinimalSession()
    {
        byte[] model =
        [
            0x08, 0x07, 0x3A, 0x3A, 0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
            0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79, 0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
            0x5A, 0x0F, 0x0A, 0x01, 0x78, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01, 0x12, 0x04, 0x0A,
            0x02, 0x08, 0x01, 0x62, 0x0F, 0x0A, 0x01, 0x79, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01,
            0x12, 0x04, 0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
        ];
        return new InferenceSession(model);
    }

    // --- Arbitrary registration for FsCheck ---

    public static Arbitrary<FailedSessionInput> FailedSessionInputArbitrary() =>
        FailedSessionInputArb();
}
