// Feature: hardware-matrix-routing, Property 11: SessionPoolKey Device Discrimination

using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Property-based tests verifying that SessionPoolKey correctly discriminates
/// keys that differ only in DeviceId or only in Provider. Two keys that differ
/// in either dimension must not be equal and should produce different hash codes
/// (with high probability).
///
/// **Validates: Requirements 3.2**
/// </summary>
public sealed class SessionPoolKeyDiscriminationPropertyTests
{
    private static readonly ExecutionProviderKind[] AllProviders =
    [
        ExecutionProviderKind.Cpu,
        ExecutionProviderKind.DirectMl,
        ExecutionProviderKind.TensorRTRtx,
        ExecutionProviderKind.OpenVino
    ];

    private static readonly string?[] OptionalModelIds = ["model-a", "model-b", null];
    private static readonly string?[] OptionalVariants = ["q4", "fp16", null];

    /// <summary>
    /// Wrapper record holding the parameters needed to construct a SessionPoolKey pair
    /// that differs only in DeviceId. Uses public types so FsCheck can expose it in
    /// public method signatures.
    /// </summary>
    public sealed record DeviceIdDiscriminationInput(
        string EngineFamily,
        string? ModelId,
        string? Variant,
        ExecutionProviderKind Provider,
        int PathSuffix,
        string GraphRole,
        int? DeviceIdA,
        int? DeviceIdB);

    /// <summary>
    /// Wrapper record holding the parameters needed to construct a SessionPoolKey pair
    /// that differs only in Provider.
    /// </summary>
    public sealed record ProviderDiscriminationInput(
        string EngineFamily,
        string? ModelId,
        string? Variant,
        int PathSuffix,
        int? DeviceId,
        string GraphRole,
        ExecutionProviderKind ProviderA,
        ExecutionProviderKind ProviderB);

    /// <summary>
    /// Generates DeviceIdDiscriminationInput where DeviceIdA != DeviceIdB.
    /// Excludes the (null, 0) and (0, null) pairs because int?.GetHashCode() returns 0
    /// for both null and 0 in .NET, causing an unavoidable hash collision in the record's
    /// combined hash code. Equality discrimination still holds for that pair.
    /// </summary>
    private static Arbitrary<DeviceIdDiscriminationInput> DeviceIdInputArb()
    {
        var gen = from engineFamily in Gen.Elements("kokoro", "whisper-onnx", "opus-mt", "silero-vad", "chatterbox")
                  from modelId in Gen.Elements(OptionalModelIds)
                  from variant in Gen.Elements(OptionalVariants)
                  from provider in Gen.Elements(AllProviders)
                  from pathSuffix in Gen.Choose(1, 1000)
                  from graphRole in Gen.Elements("default", "encoder", "decoder")
                  from deviceIdA in Gen.Elements<int?>(null, 0, 1, 2, 3, 4, 5, 6, 7)
                  from deviceIdB in Gen.Elements<int?>(null, 0, 1, 2, 3, 4, 5, 6, 7)
                  where !Equals(deviceIdA, deviceIdB)
                  // Exclude (null, 0) and (0, null) — known hash collision in .NET for int?
                  where !(deviceIdA is null && deviceIdB == 0) && !(deviceIdA == 0 && deviceIdB is null)
                  select new DeviceIdDiscriminationInput(
                      engineFamily, modelId, variant, provider, pathSuffix, graphRole, deviceIdA, deviceIdB);

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates ProviderDiscriminationInput where ProviderA != ProviderB.
    /// </summary>
    private static Arbitrary<ProviderDiscriminationInput> ProviderInputArb()
    {
        var gen = from engineFamily in Gen.Elements("kokoro", "whisper-onnx", "opus-mt", "silero-vad", "chatterbox")
                  from modelId in Gen.Elements(OptionalModelIds)
                  from variant in Gen.Elements(OptionalVariants)
                  from pathSuffix in Gen.Choose(1, 1000)
                  from deviceId in Gen.Elements<int?>(null, 0, 1, 2, 3)
                  from graphRole in Gen.Elements("default", "encoder", "decoder")
                  from providerA in Gen.Elements(AllProviders)
                  from providerB in Gen.Elements(AllProviders)
                  where providerA != providerB
                  select new ProviderDiscriminationInput(
                      engineFamily, modelId, variant, pathSuffix, deviceId, graphRole, providerA, providerB);

        return Arb.From(gen);
    }

    /// <summary>
    /// Property 11a: Keys differing only in DeviceId are NOT equal and produce different hash codes.
    ///
    /// For any two SessionPoolKey instances that are identical in all fields except DeviceId,
    /// the keys shall not be equal and shall produce different hash codes (with high probability).
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = [typeof(SessionPoolKeyDiscriminationPropertyTests)])]
    public bool DeviceId_Discrimination_DifferentDeviceIds_AreNotEqual(DeviceIdDiscriminationInput input)
    {
        var pathHash = SessionPoolKey.HashPath($@"C:\Models\model_{input.PathSuffix}.onnx");

        var keyA = new SessionPoolKey(
            input.EngineFamily, input.ModelId, input.Variant,
            input.Provider, pathHash, input.DeviceIdA, input.GraphRole);

        var keyB = new SessionPoolKey(
            input.EngineFamily, input.ModelId, input.Variant,
            input.Provider, pathHash, input.DeviceIdB, input.GraphRole);

        // Keys must not be equal
        if (keyA.Equals(keyB))
            return false;

        // Structural equality via == operator must also report not-equal
        if (keyA == keyB)
            return false;

        // Hash codes should differ (not strictly guaranteed but expected with high probability
        // for well-distributed hash functions on distinct integer values)
        return keyA.GetHashCode() != keyB.GetHashCode();
    }

    /// <summary>
    /// Property 11b: Keys differing only in Provider are NOT equal and produce different hash codes.
    ///
    /// For any two SessionPoolKey instances that are identical in all fields except Provider,
    /// the keys shall not be equal and shall produce different hash codes (with high probability).
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = [typeof(SessionPoolKeyDiscriminationPropertyTests)])]
    public bool Provider_Discrimination_DifferentProviders_AreNotEqual(ProviderDiscriminationInput input)
    {
        var pathHash = SessionPoolKey.HashPath($@"C:\Models\model_{input.PathSuffix}.onnx");

        var keyA = new SessionPoolKey(
            input.EngineFamily, input.ModelId, input.Variant,
            input.ProviderA, pathHash, input.DeviceId, input.GraphRole);

        var keyB = new SessionPoolKey(
            input.EngineFamily, input.ModelId, input.Variant,
            input.ProviderB, pathHash, input.DeviceId, input.GraphRole);

        // Keys must not be equal
        if (keyA.Equals(keyB))
            return false;

        // Structural equality via == operator must also report not-equal
        if (keyA == keyB)
            return false;

        // Hash codes should differ
        return keyA.GetHashCode() != keyB.GetHashCode();
    }

    /// <summary>
    /// Property 11c: Null DeviceId vs non-null DeviceId discrimination.
    ///
    /// A key with DeviceId=null and a key with any non-null DeviceId (all other fields identical)
    /// shall not be equal. Hash codes should differ for non-zero device IDs; the (null, 0) pair
    /// is a known .NET hash collision (int?.GetHashCode() returns 0 for both null and 0).
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public bool NullVsNonNull_DeviceId_AreNotEqual()
    {
        // Test null vs each non-null device ID (1–7) with fixed base fields.
        // DeviceId=0 vs null is a known hash collision, so we test it separately for equality only.
        const string engineFamily = "kokoro";
        const string graphRole = "default";
        var pathHash = SessionPoolKey.HashPath(@"C:\Models\test.onnx");

        foreach (var nonNullId in Enumerable.Range(0, 8))
        {
            var keyNull = new SessionPoolKey(
                engineFamily, null, null,
                ExecutionProviderKind.DirectMl, pathHash, null, graphRole);

            var keyNonNull = new SessionPoolKey(
                engineFamily, null, null,
                ExecutionProviderKind.DirectMl, pathHash, nonNullId, graphRole);

            // Equality must always discriminate null from non-null
            if (keyNull.Equals(keyNonNull))
                return false;
            if (keyNull == keyNonNull)
                return false;

            // Hash codes should differ for non-zero IDs.
            // DeviceId=0 vs null is a known .NET hash collision (both hash to 0).
            if (nonNullId != 0 && keyNull.GetHashCode() == keyNonNull.GetHashCode())
                return false;
        }

        return true;
    }

    // --- Arbitrary registration for FsCheck ---

    public static Arbitrary<DeviceIdDiscriminationInput> DeviceIdDiscriminationInputArb() =>
        DeviceIdInputArb();

    public static Arbitrary<ProviderDiscriminationInput> ProviderDiscriminationInputArb() =>
        ProviderInputArb();
}
