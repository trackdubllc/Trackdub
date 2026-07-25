using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Licensing.Tests;

// Feature: licensing-and-tier-gates, Property 7: Token file cache round-trip
// **Validates: Requirements 3.4**
public sealed class TokenFileCacheRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public TokenFileCacheRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trackdub-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [Property(MaxTest = 100)]
    public Property Token_written_and_read_back_is_byte_identical()
    {
        // Generate valid JWT-like token strings: base64url characters and dots only.
        // Real license tokens are compact JWTs (three base64url segments separated by dots)
        // which never contain whitespace or control characters.
        const string base64UrlChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        var segmentGen = Gen.Choose(1, 200)
            .SelectMany(len => Gen.ArrayOf(len, Gen.Elements(base64UrlChars.ToCharArray())))
            .Select(chars => new string(chars));

        var tokenGen = from header in segmentGen
                       from payload in segmentGen
                       from signature in segmentGen
                       select $"{header}.{payload}.{signature}";

        return Prop.ForAll(tokenGen.ToArbitrary(), token =>
        {
            var store = new LicenseFileStore(_tempDir);
            store.WriteToken(token);
            var readBack = store.ReadToken();

            return (readBack == token)
                .Label($"Expected length {token.Length}, got length {readBack?.Length}");
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }
}
