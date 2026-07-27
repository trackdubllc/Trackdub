using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Trackdub.Licensing.Tests;

// Covers the ILicenseSignatureTrustStore seam: a consuming product can supply its own
// multi-key trust policy without patching Trackdub.Licensing internals.
public sealed class LicenseSignatureTrustStoreTests
{
    [Fact]
    public async Task Token_signed_with_trust_store_key_verifies_and_resolves_pro_tier()
    {
        var tempBase = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();

            var token = TestTokenBuilder.BuildSignedToken(
                sub: "user-1",
                tier: "pro",
                machines: ["sha256:current"],
                iat: 1_700_000_000,
                exp: 2_000_000_000,
                keyId: "ring-key-1",
                signingKey: key);

            var store = new LicenseFileStore(tempBase);
            store.WriteToken(token);

            var trustStore = new FakeTrustStore(("ring-key-1", publicKeyPem));
            var service = new LicenseService(
                new StaticFingerprintProvider("sha256:current"),
                NullLogger<LicenseService>.Instance,
                trustStore);

            var result = await service.InitializeAsync();

            Assert.Equal(LicenseTier.Pro, result.Tier);
            Assert.Null(result.DegradationReason);
            var requestedKeyId = Assert.Single(trustStore.RequestedKeyIds);
            Assert.Equal("ring-key-1", requestedKeyId);
        }
        finally
        {
            Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public async Task Unknown_key_id_is_rejected_even_with_otherwise_valid_signature()
    {
        var tempBase = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var token = TestTokenBuilder.BuildSignedToken(
                sub: "user-1",
                tier: "pro",
                machines: ["sha256:current"],
                iat: 1_700_000_000,
                exp: 2_000_000_000,
                keyId: "revoked-key",
                signingKey: key);

            var store = new LicenseFileStore(tempBase);
            store.WriteToken(token);

            // Trust store knows nothing about "revoked-key" -- simulates an unknown or
            // revoked key id. ResolvePublicKeyPem returning null must fail verification
            // closed, not fall back to any other key.
            var trustStore = new FakeTrustStore();
            var service = new LicenseService(
                new StaticFingerprintProvider("sha256:current"),
                NullLogger<LicenseService>.Instance,
                trustStore);

            var result = await service.InitializeAsync();

            Assert.Equal(LicenseTier.Free, result.Tier);
            Assert.Equal("Invalid signature", result.DegradationReason);
        }
        finally
        {
            Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public async Task Signature_from_wrong_key_fails_even_when_key_id_resolves()
    {
        var tempBase = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var differentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var token = TestTokenBuilder.BuildSignedToken(
                sub: "user-1",
                tier: "pro",
                machines: ["sha256:current"],
                iat: 1_700_000_000,
                exp: 2_000_000_000,
                keyId: "ring-key-1",
                signingKey: signingKey);

            var store = new LicenseFileStore(tempBase);
            store.WriteToken(token);

            // Trust store resolves "ring-key-1" to a *different* public key than the one
            // that actually signed the token.
            var trustStore = new FakeTrustStore(("ring-key-1", differentKey.ExportSubjectPublicKeyInfoPem()));
            var service = new LicenseService(
                new StaticFingerprintProvider("sha256:current"),
                NullLogger<LicenseService>.Instance,
                trustStore);

            var result = await service.InitializeAsync();

            Assert.Equal(LicenseTier.Free, result.Tier);
            Assert.Equal("Invalid signature", result.DegradationReason);
        }
        finally
        {
            Directory.Delete(tempBase, true);
        }
    }

    private sealed class FakeTrustStore : ILicenseSignatureTrustStore
    {
        private readonly Dictionary<string, string> _keysByKeyId;

        public FakeTrustStore(params (string KeyId, string PublicKeyPem)[] keys)
        {
            _keysByKeyId = keys.ToDictionary(k => k.KeyId, k => k.PublicKeyPem);
        }

        public List<string?> RequestedKeyIds { get; } = [];

        public string? ResolvePublicKeyPem(string? keyId)
        {
            RequestedKeyIds.Add(keyId);
            return keyId is not null && _keysByKeyId.TryGetValue(keyId, out var pem) ? pem : null;
        }
    }

    private sealed class StaticFingerprintProvider(string fingerprint) : IHardwareFingerprintProvider
    {
        public string GetFingerprint() => fingerprint;
    }
}
