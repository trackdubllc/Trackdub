using Microsoft.Extensions.Logging;

namespace Trackdub.Licensing;

/// <summary>
/// Orchestrates license initialization: load file → parse → validate → resolve tier.
/// Implements both ILicenseInitializer and ILicenseTierProvider.
/// Thread-safe after initialization.
/// </summary>
public sealed class LicenseService : ILicenseInitializer, ILicenseTierProvider
{
    private readonly LicenseFileStore _fileStore;
    private readonly LicenseTokenParser _parser;
    private readonly LicenseTokenValidator _validator;
    private readonly IHardwareFingerprintProvider _fingerprintProvider;
    private readonly ILogger<LicenseService> _logger;

    private LicenseValidationResult _validationResult;

    public LicenseService(
        IHardwareFingerprintProvider fingerprintProvider,
        ILogger<LicenseService> logger)
        : this(fingerprintProvider, logger, trustStore: null)
    {
    }

    /// <param name="trustStore">
    /// Signature trust policy. When null, tokens are verified against the single
    /// embedded development public key, matching the two-argument constructor's
    /// behavior. A consuming product that needs multi-key rotation or revocation
    /// supplies its own <see cref="ILicenseSignatureTrustStore"/> implementation here.
    /// </param>
    public LicenseService(
        IHardwareFingerprintProvider fingerprintProvider,
        ILogger<LicenseService> logger,
        ILicenseSignatureTrustStore? trustStore)
    {
        _fileStore = new LicenseFileStore();
        _parser = new LicenseTokenParser();
        _validator = trustStore is not null
            ? new LicenseTokenValidator(trustStore)
            : new LicenseTokenValidator();
        _fingerprintProvider = fingerprintProvider;
        _logger = logger;
        _validationResult = new LicenseValidationResult(LicenseTier.Free, null, 0, 0, null, null);
    }

    /// <summary>
    /// Internal constructor for testing — accepts custom collaborators.
    /// </summary>
    internal LicenseService(
        LicenseFileStore fileStore,
        LicenseTokenParser parser,
        LicenseTokenValidator validator,
        IHardwareFingerprintProvider fingerprintProvider,
        ILogger<LicenseService> logger)
    {
        _fileStore = fileStore;
        _parser = parser;
        _validator = validator;
        _fingerprintProvider = fingerprintProvider;
        _logger = logger;
        _validationResult = new LicenseValidationResult(LicenseTier.Free, null, 0, 0, null, null);
    }

    public LicenseTier CurrentTier => _validationResult.Tier;

    public LicenseValidationResult ValidationResult => _validationResult;

    public Task<LicenseValidationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Load token from file
            var token = _fileStore.ReadToken();
            if (token is null)
            {
                _logger.LogDebug("No license token file found. Resolving to Free tier.");
                _validationResult = new LicenseValidationResult(LicenseTier.Free, null, 0, 0, null, null);
                return Task.FromResult(_validationResult);
            }

            // 2. Parse token
            var claims = _parser.Parse(token);
            if (claims is null)
            {
                _logger.LogWarning("License token is malformed or unparseable.");
                _validationResult = new LicenseValidationResult(LicenseTier.Free, null, 0, 0, null, "Token is malformed");
                return Task.FromResult(_validationResult);
            }

            // 3. Verify signature
            var sigParts = _parser.GetSignatureParts(token);
            if (sigParts is null || !_validator.VerifySignature(claims.KeyId, sigParts.Value.SigningInput, sigParts.Value.Signature))
            {
                _logger.LogWarning("License token signature verification failed.");
                _validationResult = new LicenseValidationResult(LicenseTier.Free, claims.Sub, 0, 0, null, "Invalid signature");
                return Task.FromResult(_validationResult);
            }

            // 4. Check expiry (dev-unlimited tokens omit exp)
            DateTimeOffset? expiresAt = claims.Exp is not null
                ? DateTimeOffset.FromUnixTimeSeconds(claims.Exp.Value)
                : null;

            if (!claims.DevUnlimited
                && claims.Exp is not null
                && claims.Exp.Value < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                _logger.LogInformation("License token has expired.");
                _validationResult = new LicenseValidationResult(LicenseTier.Free, claims.Sub, claims.Machines.Count, 0, expiresAt, "Token expired");
                return Task.FromResult(_validationResult);
            }

            // 5. Validate fingerprint (skipped for signed dev-unlimited tokens)
            var currentFingerprint = _fingerprintProvider.GetFingerprint();
            var machineMatch = claims.Machines.Any(m =>
                string.Equals(m, currentFingerprint, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m, $"sha256:{currentFingerprint}", StringComparison.OrdinalIgnoreCase));

            if (!claims.DevUnlimited && !machineMatch)
            {
                _logger.LogInformation("Current machine fingerprint does not match any registered machine.");
                _validationResult = new LicenseValidationResult(LicenseTier.Free, claims.Sub, claims.Machines.Count, 2, expiresAt, "Machine not registered");
                return Task.FromResult(_validationResult);
            }

            // 6. Resolve tier
            var tier = claims.Tier.Equals("pro", StringComparison.OrdinalIgnoreCase)
                ? LicenseTier.Pro
                : LicenseTier.Free;

            var machinesUsed = claims.DevUnlimited
                ? Math.Max(claims.Machines.Count, machineMatch ? 1 : 0)
                : claims.Machines.Count;

            _validationResult = claims.DevUnlimited
                ? new LicenseValidationResult(tier, claims.Sub, machinesUsed, 0, null, null, UnlimitedActivations: true)
                : new LicenseValidationResult(tier, claims.Sub, claims.Machines.Count, 2, expiresAt, null);
            return Task.FromResult(_validationResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "License validation failed unexpectedly.");
            _validationResult = new LicenseValidationResult(
                LicenseTier.Free, null, 0, 0, null, $"License validation error: {ex.Message}");
            return Task.FromResult(_validationResult);
        }
    }
}
