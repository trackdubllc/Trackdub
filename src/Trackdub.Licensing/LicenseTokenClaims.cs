namespace Trackdub.Licensing;

/// <summary>
/// Strongly-typed representation of the JWT payload claims extracted from a license token.
/// </summary>
internal sealed record LicenseTokenClaims(
    string Sub,
    string Tier,
    IReadOnlyList<string> Machines,
    long Iat,
    long? Exp,
    bool DevUnlimited = false);
