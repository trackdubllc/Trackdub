using System.Text;
using System.Text.Json;

namespace Trackdub.Licensing;

/// <summary>
/// Manual JWT parser that splits compact tokens, base64url-decodes segments,
/// and deserializes the payload into <see cref="LicenseTokenClaims"/>.
/// No third-party JWT libraries are used.
/// </summary>
internal sealed class LicenseTokenParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses a compact JWT token and extracts the claims payload.
    /// Returns null if the token is malformed or unparseable.
    /// </summary>
    public LicenseTokenClaims? Parse(string compactToken)
    {
        if (string.IsNullOrWhiteSpace(compactToken))
            return null;

        var parts = compactToken.Split('.');
        if (parts.Length != 3)
            return null;

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            var payload = JsonSerializer.Deserialize<PayloadDto>(payloadBytes, JsonOptions);

            if (payload is null || payload.Sub is null || payload.Tier is null)
                return null;

            return new LicenseTokenClaims(
                Sub: payload.Sub,
                Tier: payload.Tier,
                Machines: payload.Machines ?? [],
                Iat: payload.Iat,
                Exp: payload.Exp,
                DevUnlimited: payload.DevUnlimited ?? false);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the raw signing input (header.payload as UTF-8 bytes) and decoded signature bytes.
    /// Used by the validator for signature verification.
    /// </summary>
    public (byte[] SigningInput, byte[] Signature)? GetSignatureParts(string compactToken)
    {
        if (string.IsNullOrWhiteSpace(compactToken))
            return null;

        var parts = compactToken.Split('.');
        if (parts.Length != 3)
            return null;

        try
        {
            // Signing input is the raw "header.payload" string as UTF-8 bytes
            var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
            var signature = Base64UrlDecode(parts[2]);
            return (signingInput, signature);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Internal DTO for JSON deserialization of the JWT payload.
    /// </summary>
    private sealed record PayloadDto
    {
        public string? Sub { get; init; }
        public string? Tier { get; init; }
        public IReadOnlyList<string>? Machines { get; init; }
        public long Iat { get; init; }
        public long? Exp { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("dev_unlimited")]
        public bool? DevUnlimited { get; init; }
    }
}
