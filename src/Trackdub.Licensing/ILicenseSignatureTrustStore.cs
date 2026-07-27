namespace Trackdub.Licensing;

/// <summary>
/// Resolves the trusted public key used to verify a license token's ES256 signature.
/// </summary>
/// <remarks>
/// The public core verifies tokens against a single embedded development key by
/// default (see the parameterless <see cref="LicenseService"/> constructor). A
/// consuming product that needs multi-key rotation or revocation supplies its own
/// trust policy by implementing this interface and passing it to
/// <see cref="LicenseService"/> — <see cref="Trackdub.Licensing"/> itself never
/// implements or requires one.
/// </remarks>
public interface ILicenseSignatureTrustStore
{
    /// <summary>
    /// Resolves the PEM-encoded ECDSA P-256 public key trusted for the given key id,
    /// or null to reject the token — e.g. because the key id is unknown, or the key
    /// has been revoked.
    /// </summary>
    /// <param name="keyId">
    /// The key id taken from the token's unverified claims. It is itself untrusted:
    /// callers must not treat it as authenticated until the key it resolves to goes
    /// on to successfully verify the token's signature. May be null for tokens that
    /// carry no key id; implementations decide whether that resolves to a default
    /// key or is rejected.
    /// </param>
    string? ResolvePublicKeyPem(string? keyId);
}
