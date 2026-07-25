using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Trackdub.Licensing.Tests;

public sealed class DevUnlimitedTokenTests
{
    [Fact]
    public async Task Dev_unlimited_token_resolves_to_pro_without_fingerprint_match()
    {
        var token = TestTokenBuilder.BuildSignedToken(
            sub: "dev-unlimited-local-license",
            tier: "pro",
            machines: ["sha256:other_machine"],
            iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600,
            exp: null,
            devUnlimited: true);

        var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var tokenDir = Path.Combine(tempBase, "Trackdub");
        Directory.CreateDirectory(tokenDir);
        File.WriteAllText(Path.Combine(tokenDir, "license.jwt"), token);

        try
        {
            var fileStore = new LicenseFileStore(tempBase);
            var parser = new LicenseTokenParser();
            var validator = new LicenseTokenValidator();
            var fingerprintProvider = new FakeHardwareFingerprintProvider("current_machine_fp");
            var service = new LicenseService(
                fileStore, parser, validator, fingerprintProvider,
                NullLogger<LicenseService>.Instance);

            var result = await service.InitializeAsync();

            Assert.Equal(LicenseTier.Pro, result.Tier);
            Assert.True(result.UnlimitedActivations);
            Assert.Null(result.ExpiresAt);
            Assert.Null(result.DegradationReason);
        }
        finally
        {
            Directory.Delete(tempBase, true);
        }
    }

    private sealed class FakeHardwareFingerprintProvider : IHardwareFingerprintProvider
    {
        private readonly string _fingerprint;
        public FakeHardwareFingerprintProvider(string fingerprint) => _fingerprint = fingerprint;
        public string GetFingerprint() => _fingerprint;
    }
}
