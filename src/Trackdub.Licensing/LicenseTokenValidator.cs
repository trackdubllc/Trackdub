using System.Security.Cryptography;

namespace Trackdub.Licensing;

/// <summary>
/// Verifies ES256 (ECDSA P-256 + SHA-256) signatures on license tokens
/// using the embedded public key. Never throws — returns false on any error.
/// </summary>
internal sealed class LicenseTokenValidator
{
    // Development public key (PEM). Matches services/activation-service/dev/dev-signing-key.pem.
    // Before production deploy: generate a new key pair, set SIGNING_KEY on the Worker, and replace
    // this PEM with the corresponding public key (see activation-service README).
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEuuyxWzfg3/qj2KTYIw3tMJru1vzI
        j6+mdO7zCqiMf61lJAtyeERf1G0dSxncz9k7OY4Pw8WXG/XCSkBcqb5JpQ==
        -----END PUBLIC KEY-----
        """;

    private readonly ECDsa _ecdsa;

    /// <summary>
    /// Creates a validator using the embedded production public key.
    /// </summary>
    public LicenseTokenValidator()
    {
        _ecdsa = ECDsa.Create();
        _ecdsa.ImportFromPem(PublicKeyPem);
    }

    /// <summary>
    /// Creates a validator with a custom public key PEM for testing.
    /// </summary>
    internal LicenseTokenValidator(string publicKeyPem)
    {
        _ecdsa = ECDsa.Create();
        _ecdsa.ImportFromPem(publicKeyPem);
    }

    /// <summary>
    /// Verifies the ES256 signature over the signing input bytes.
    /// Returns true if the signature is valid, false otherwise.
    /// Never throws.
    /// </summary>
    public bool VerifySignature(byte[] signingInput, byte[] signature)
    {
        try
        {
            return _ecdsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
