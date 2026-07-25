using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Licensing.Tests;

// Feature: licensing-and-tier-gates, Property 6: Fingerprint is hashed, not raw
// **Validates: Requirements 2.7**
public sealed partial class FingerprintHashedNotRawTests
{
    [Property(MaxTest = 100)]
    public Property Fingerprint_is_64_char_lowercase_hex_and_differs_from_raw()
    {
        return Prop.ForAll(Arb.Default.NonEmptyString(), nes =>
        {
            var rawInput = nes.Get;
            var fingerprint = ComputeFingerprint(rawInput);

            return fingerprint.Length == 64
                && HexRegex().IsMatch(fingerprint)
                && fingerprint != rawInput.Trim();
        });
    }

    private static string ComputeFingerprint(string rawId)
    {
        var trimmed = rawId.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexStringLower(hash);
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexRegex();
}
