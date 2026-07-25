using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Licensing.Tests;

// Feature: licensing-and-tier-gates, Property 1: Token signature and claim round-trip
// **Validates: Requirements 1.1, 1.2**
public sealed class TokenSignatureRoundTripTests
{
    [Property(MaxTest = 100)]
    public Property Signed_token_round_trips_claims_through_parse_and_validate()
    {
        var claimsGen = from sub in Arb.Default.NonEmptyString().Generator.Select(s => s.Get)
                        from tier in Gen.Elements("free", "pro")
                        from machines in Gen.ListOf(Gen.Elements("sha256:aabb", "sha256:ccdd", "sha256:eeff", "sha256:1122"))
                            .Select(ms => (IReadOnlyList<string>)ms.Distinct().ToList())
                        from iat in Gen.Choose(1_700_000_000, 1_900_000_000).Select(i => (long)i)
                        from exp in Gen.Frequency(
                            Tuple.Create(3, Gen.Choose(2_000_000_000, 2_100_000_000).Select(i => (long?)i)),
                            Tuple.Create(1, Gen.Constant((long?)null)))
                        select (sub, tier, machines, iat, exp);

        return Prop.ForAll(claimsGen.ToArbitrary(), input =>
        {
            var (sub, tier, machines, iat, exp) = input;

            // Arrange: build a signed token
            var token = TestTokenBuilder.BuildSignedToken(sub, tier, machines, iat, exp);

            // Act: parse and validate
            var parser = new LicenseTokenParser();
            var validator = new LicenseTokenValidator();

            var claims = parser.Parse(token);
            var sigParts = parser.GetSignatureParts(token);

            // Assert: parsing succeeds
            if (claims is null || sigParts is null)
                return false.Label("Parse returned null");

            // Assert: signature is valid
            var signatureValid = validator.VerifySignature(sigParts.Value.SigningInput, sigParts.Value.Signature);
            if (!signatureValid)
                return false.Label("Signature validation failed");

            // Assert: claims round-trip exactly
            var subMatch = claims.Sub == sub;
            var tierMatch = claims.Tier == tier;
            var machinesMatch = claims.Machines.SequenceEqual(machines);
            var iatMatch = claims.Iat == iat;
            var expMatch = claims.Exp == exp;

            return (subMatch && tierMatch && machinesMatch && iatMatch && expMatch)
                .Label($"Claims mismatch: sub={subMatch}, tier={tierMatch}, machines={machinesMatch}, iat={iatMatch}, exp={expMatch}");
        });
    }
}

/// <summary>
/// Helper to build signed test tokens using the test ECC P-256 private key
/// that matches the public key embedded in LicenseTokenValidator.
/// </summary>
internal static class TestTokenBuilder
{
    private static readonly ECDsa Ecdsa = CreateKey();

    private static ECDsa CreateKey()
    {
        // PKCS#8 format EC P-256 private key (matching the public key in LicenseTokenValidator)
        const string pem =
            "-----BEGIN PRIVATE KEY-----\n" +
            "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg1uCjeDrK6xNeCFm2\n" +
            "ZWQjGwD5RjUmd6ZIDmurpSLKi5ShRANCAAS67LFbN+Df+qPYpNgjDe0wmu7W/MiP\n" +
            "r6Z07vMKqIx/rWUkC3J4RF/UbR1LGdzP2Ts5jg/DxZcb9cJKQFypvkml\n" +
            "-----END PRIVATE KEY-----";

        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return ecdsa;
    }

    public static string BuildSignedToken(
        string sub,
        string tier,
        IReadOnlyList<string> machines,
        long iat,
        long? exp,
        bool devUnlimited = false)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT" }));
        var payloadObj = new Dictionary<string, object?>
        {
            ["sub"] = sub,
            ["tier"] = tier,
            ["machines"] = machines,
            ["iat"] = iat,
            ["exp"] = exp
        };
        if (devUnlimited)
        {
            payloadObj["dev_unlimited"] = true;
        }
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payloadObj));
        var signingInput = $"{header}.{payload}";

        var signatureBytes = Ecdsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256);
        var signature = Base64UrlEncode(signatureBytes);

        return $"{header}.{payload}.{signature}";
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
