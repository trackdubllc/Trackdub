using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Licensing.Tests;

// Feature: licensing-and-tier-gates, Property 4: Fingerprint determinism
// **Validates: Requirements 2.1, 2.2**
public sealed class FingerprintDeterminismTests
{
    [Property(MaxTest = 100)]
    public Property Fingerprint_is_deterministic_for_any_input()
    {
        return Prop.ForAll(Arb.Default.NonEmptyString(), nes =>
        {
            var input = nes.Get;
            var hash1 = ComputeFingerprint(input);
            var hash2 = ComputeFingerprint(input);
            return hash1 == hash2;
        });
    }

    private static string ComputeFingerprint(string rawId)
    {
        var trimmed = rawId.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexStringLower(hash);
    }
}
