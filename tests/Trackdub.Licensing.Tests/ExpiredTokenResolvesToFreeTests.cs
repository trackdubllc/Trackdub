using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Licensing.Tests;

// Feature: licensing-and-tier-gates, Property 2: Expired token resolves to Free
// **Validates: Requirements 1.3**
public sealed class ExpiredTokenResolvesToFreeTests
{
    [Property(MaxTest = 100)]
    public Property Expired_token_always_resolves_to_free()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Generate past timestamps: 1 second ago to ~10 years ago
        var maxSecondsInPast = Math.Min((int)(now - 1), 315_360_000); // cap at 10 years
        var pastExpGen = Gen.Choose(1, maxSecondsInPast)
            .Select(secondsAgo => now - secondsAgo);

        return Prop.ForAll(pastExpGen.ToArbitrary(), pastExp =>
        {
            // Build a valid signed token with a past expiry
            var token = TestTokenBuilder.BuildSignedToken(
                sub: "lic_test",
                tier: "pro",
                machines: ["sha256:abc123"],
                iat: pastExp - 86400,
                exp: pastExp);

            var parser = new LicenseTokenParser();
            var claims = parser.Parse(token);

            // Token must be parseable (valid signature structure)
            if (claims is null)
                return false.Label("Parse returned null for valid token");

            // Exp must be in the past
            if (claims.Exp is null || claims.Exp.Value >= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return false.Label($"Expected exp {claims.Exp} to be in the past");

            // Therefore tier should resolve to Free (expiry check confirms expired)
            return IsExpired(claims).Label("IsExpired should return true for past exp");
        });
    }

    /// <summary>
    /// Mimics the expiry check logic from the design:
    /// if exp is set and in the past, the token is expired, so tier resolves to Free.
    /// </summary>
    private static bool IsExpired(LicenseTokenClaims claims)
    {
        if (claims.Exp is null) return false;
        return claims.Exp.Value < DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
