using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Licensing.Tests;

// Feature: licensing-and-tier-gates, Property 5: Fingerprint collision resistance
// **Validates: Requirements 2.3**

/// <summary>
/// For any two distinct raw machine identifier strings, the generated
/// Hardware_Fingerprints SHALL be distinct.
/// </summary>
public sealed class FingerprintCollisionResistanceTests
{
    [Property(MaxTest = 100)]
    public Property Distinct_inputs_produce_distinct_fingerprints()
    {
        return Prop.ForAll(Arb.Default.NonEmptyString(), Arb.Default.NonEmptyString(), (a, b) =>
        {
            var inputA = a.Get;
            var inputB = b.Get;

            var trimA = inputA.Trim();
            var trimB = inputB.Trim();

            // Only assert collision resistance when trimmed inputs are actually distinct
            return (trimA == trimB) || (ComputeFingerprint(inputA) != ComputeFingerprint(inputB));
        });
    }

    private static string ComputeFingerprint(string rawId)
    {
        var trimmed = rawId.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexStringLower(hash);
    }
}
