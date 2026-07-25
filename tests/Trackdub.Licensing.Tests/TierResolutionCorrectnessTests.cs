using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Trackdub.Licensing.Tests;

// Feature: licensing-and-tier-gates, Property 3: Tier resolution correctness
// **Validates: Requirements 1.5, 1.6, 3.3**
public sealed class TierResolutionCorrectnessTests
{
    [Property(MaxTest = 100)]
    public Property Tier_is_pro_iff_valid_signature_and_fingerprint_match()
    {
        // Generate scenarios covering: tier claim, fingerprint match/mismatch, valid/invalid signature
        var scenarioGen = from tier in Gen.Elements("pro", "free")
                          from fingerprint in Gen.Elements(
                              "sha256:aabb", "sha256:ccdd", "sha256:eeff", "sha256:1122", "sha256:face")
                          from shouldMatchFingerprint in Arb.Default.Bool().Generator
                          from shouldHaveValidSignature in Arb.Default.Bool().Generator
                          select (tier, fingerprint, shouldMatchFingerprint, shouldHaveValidSignature);

        return Prop.ForAll(scenarioGen.ToArbitrary(), scenario =>
        {
            var (tier, tokenFingerprint, shouldMatchFingerprint, shouldHaveValidSignature) = scenario;

            // The fingerprint the "current machine" reports
            var currentFingerprint = shouldMatchFingerprint
                ? tokenFingerprint
                : tokenFingerprint + "_different";

            // Build token: either validly signed or with corrupted signature
            string token;
            if (shouldHaveValidSignature)
            {
                token = TestTokenBuilder.BuildSignedToken(
                    sub: "lic_test",
                    tier: tier,
                    machines: [tokenFingerprint],
                    iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600,
                    exp: null); // no expiry
            }
            else
            {
                // Build a valid token then corrupt the signature
                token = TestTokenBuilder.BuildSignedToken(
                    sub: "lic_test",
                    tier: tier,
                    machines: [tokenFingerprint],
                    iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600,
                    exp: null);
                // Corrupt signature by replacing last few characters
                token = token[..^4] + "ZZZZ";
            }

            // Write token to temp directory using the expected file store path structure
            var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var tokenDir = Path.Combine(tempBase, "Trackdub");
            Directory.CreateDirectory(tokenDir);
            File.WriteAllText(Path.Combine(tokenDir, "license.jwt"), token);

            try
            {
                var fileStore = new LicenseFileStore(tempBase);
                var parser = new LicenseTokenParser();
                var validator = new LicenseTokenValidator();
                var fingerprintProvider = new FakeHardwareFingerprintProvider(currentFingerprint);
                var service = new LicenseService(
                    fileStore, parser, validator, fingerprintProvider,
                    NullLogger<LicenseService>.Instance);

                var result = service.InitializeAsync().GetAwaiter().GetResult();

                // Pro iff: valid signature AND fingerprint matches AND tier claim is "pro"
                var expectedTier = (shouldHaveValidSignature && shouldMatchFingerprint && tier == "pro")
                    ? LicenseTier.Pro
                    : LicenseTier.Free;

                return (result.Tier == expectedTier)
                    .Label($"Expected {expectedTier} but got {result.Tier} " +
                           $"(validSig={shouldHaveValidSignature}, fpMatch={shouldMatchFingerprint}, tier={tier})");
            }
            finally
            {
                Directory.Delete(tempBase, true);
            }
        });
    }

    [Property(MaxTest = 100)]
    public Property Expired_token_resolves_to_free_regardless_of_fingerprint_match()
    {
        // Even with valid signature and matching fingerprint, expired tokens → Free
        var scenarioGen = from fingerprint in Gen.Elements(
                              "sha256:aabb", "sha256:ccdd", "sha256:eeff")
                          from secondsAgo in Gen.Choose(1, 315_360_000)
                          select (fingerprint, secondsAgo);

        return Prop.ForAll(scenarioGen.ToArbitrary(), scenario =>
        {
            var (fingerprint, secondsAgo) = scenario;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var pastExp = now - secondsAgo;

            var token = TestTokenBuilder.BuildSignedToken(
                sub: "lic_test",
                tier: "pro",
                machines: [fingerprint],
                iat: pastExp - 3600,
                exp: pastExp);

            var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var tokenDir = Path.Combine(tempBase, "Trackdub");
            Directory.CreateDirectory(tokenDir);
            File.WriteAllText(Path.Combine(tokenDir, "license.jwt"), token);

            try
            {
                var fileStore = new LicenseFileStore(tempBase);
                var parser = new LicenseTokenParser();
                var validator = new LicenseTokenValidator();
                var fingerprintProvider = new FakeHardwareFingerprintProvider(fingerprint);
                var service = new LicenseService(
                    fileStore, parser, validator, fingerprintProvider,
                    NullLogger<LicenseService>.Instance);

                var result = service.InitializeAsync().GetAwaiter().GetResult();

                return (result.Tier == LicenseTier.Free)
                    .Label($"Expected Free for expired token but got {result.Tier}");
            }
            finally
            {
                Directory.Delete(tempBase, true);
            }
        });
    }

    /// <summary>
    /// Fake fingerprint provider for testing that returns a predetermined value.
    /// </summary>
    private sealed class FakeHardwareFingerprintProvider : IHardwareFingerprintProvider
    {
        private readonly string _fingerprint;
        public FakeHardwareFingerprintProvider(string fingerprint) => _fingerprint = fingerprint;
        public string GetFingerprint() => _fingerprint;
    }
}
