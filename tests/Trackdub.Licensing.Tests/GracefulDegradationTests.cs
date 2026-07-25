using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Trackdub.Licensing.Tests;

// Feature: licensing-and-tier-gates, Property 10: Graceful degradation — never throws
// **Validates: Requirements 6.1, 6.2, 6.3, 6.4, 1.4**
public sealed class GracefulDegradationTests : IDisposable
{
    private readonly string _tempDir;

    public GracefulDegradationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trackdub-degradation-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [Property(MaxTest = 100)]
    public Property InitializeAsync_never_throws_for_arbitrary_token_content()
    {
        return Prop.ForAll(Arb.Default.String(), tokenContent =>
        {
            var store = new LicenseFileStore(_tempDir);

            if (tokenContent is not null)
            {
                store.WriteToken(tokenContent);
            }
            else
            {
                store.DeleteToken();
            }

            var service = new LicenseService(
                store,
                new LicenseTokenParser(),
                new LicenseTokenValidator(),
                new StaticFingerprintProvider("sha256:test"),
                NullLogger<LicenseService>.Instance);

            var result = service.InitializeAsync().GetAwaiter().GetResult();

            return (result is not null && result.Tier == LicenseTier.Free)
                .Label($"Expected non-null result with Free tier, got: {result?.Tier}");
        });
    }

    [Property(MaxTest = 100)]
    public Property InitializeAsync_never_throws_when_fingerprint_provider_throws()
    {
        var tokenGen = from sub in Arb.Default.NonEmptyString().Generator.Select(s => s.Get)
                       from tier in Gen.Elements("free", "pro")
                       from machines in Gen.ListOf(Gen.Elements("sha256:aabb", "sha256:ccdd"))
                           .Select(ms => (IReadOnlyList<string>)ms.Distinct().ToList())
                       from iat in Gen.Choose(1_700_000_000, 1_900_000_000).Select(i => (long)i)
                       from exp in Gen.Frequency(
                           Tuple.Create(2, Gen.Choose(2_000_000_000, 2_100_000_000).Select(i => (long?)i)),
                           Tuple.Create(1, Gen.Constant((long?)null)))
                       select TestTokenBuilder.BuildSignedToken(sub, tier, machines, iat, exp);

        return Prop.ForAll(tokenGen.ToArbitrary(), token =>
        {
            var store = new LicenseFileStore(_tempDir);
            store.WriteToken(token);

            var service = new LicenseService(
                store,
                new LicenseTokenParser(),
                new LicenseTokenValidator(),
                new ThrowingFingerprintProvider(),
                NullLogger<LicenseService>.Instance);

            var result = service.InitializeAsync().GetAwaiter().GetResult();

            return (result is not null && result.Tier == LicenseTier.Free)
                .Label($"Expected non-null result with Free tier when fingerprint throws, got: {result?.Tier}");
        });
    }

    [Property(MaxTest = 100)]
    public Property InitializeAsync_never_throws_for_invalid_jwt_structures()
    {
        // Generate strings with wrong dot counts: no dots, one dot, two dots (but garbage), four dots
        var invalidStructureGen = Gen.OneOf(
            // No dots at all
            Arb.Default.NonEmptyString().Generator.Select(s => s.Get.Replace(".", "")),
            // One dot
            Gen.Two(Arb.Default.NonEmptyString().Generator)
                .Select(t => $"{t.Item1.Get.Replace(".", "")}.{t.Item2.Get.Replace(".", "")}"),
            // Four dots
            Gen.ArrayOf(5, Arb.Default.NonEmptyString().Generator)
                .Select(parts => string.Join(".", parts.Select(p => p.Get.Replace(".", "")))));

        return Prop.ForAll(invalidStructureGen.ToArbitrary(), invalidToken =>
        {
            var store = new LicenseFileStore(_tempDir);
            store.WriteToken(invalidToken);

            var service = new LicenseService(
                store,
                new LicenseTokenParser(),
                new LicenseTokenValidator(),
                new StaticFingerprintProvider("sha256:test"),
                NullLogger<LicenseService>.Instance);

            var result = service.InitializeAsync().GetAwaiter().GetResult();

            return (result is not null && result.Tier == LicenseTier.Free)
                .Label($"Expected Free tier for invalid JWT structure, got: {result?.Tier}");
        });
    }

    [Property(MaxTest = 100)]
    public Property InitializeAsync_never_throws_for_random_byte_content()
    {
        return Prop.ForAll(Arb.Default.Byte().Generator.ArrayOf().ToArbitrary(), randomBytes =>
        {
            var store = new LicenseFileStore(_tempDir);

            if (randomBytes.Length > 0)
            {
                // Write raw bytes as a string — simulates corrupted file content
                var content = Convert.ToBase64String(randomBytes);
                store.WriteToken(content);
            }
            else
            {
                store.DeleteToken();
            }

            var service = new LicenseService(
                store,
                new LicenseTokenParser(),
                new LicenseTokenValidator(),
                new StaticFingerprintProvider("sha256:test"),
                NullLogger<LicenseService>.Instance);

            var result = service.InitializeAsync().GetAwaiter().GetResult();

            return (result is not null && result.Tier == LicenseTier.Free)
                .Label($"Expected Free tier for random bytes, got: {result?.Tier}");
        });
    }

    [Property(MaxTest = 100)]
    public Property InitializeAsync_never_throws_for_valid_base64_but_invalid_json()
    {
        // Three base64url-encoded segments that decode but aren't valid JSON or claims
        var garbageBase64Gen = Gen.ArrayOf(3, Arb.Default.NonEmptyString().Generator)
            .Select(parts =>
            {
                var encoded = parts.Select(p => Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(p.Get)));
                return string.Join(".", encoded);
            });

        return Prop.ForAll(garbageBase64Gen.ToArbitrary(), token =>
        {
            var store = new LicenseFileStore(_tempDir);
            store.WriteToken(token);

            var service = new LicenseService(
                store,
                new LicenseTokenParser(),
                new LicenseTokenValidator(),
                new StaticFingerprintProvider("sha256:test"),
                NullLogger<LicenseService>.Instance);

            var result = service.InitializeAsync().GetAwaiter().GetResult();

            return (result is not null && result.Tier == LicenseTier.Free)
                .Label($"Expected Free tier for valid base64 but invalid JSON, got: {result?.Tier}");
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class ThrowingFingerprintProvider : IHardwareFingerprintProvider
    {
        public string GetFingerprint() =>
            throw new InvalidOperationException("Simulated fingerprint failure");
    }

    private sealed class StaticFingerprintProvider(string fingerprint) : IHardwareFingerprintProvider
    {
        public string GetFingerprint() => fingerprint;
    }
}
