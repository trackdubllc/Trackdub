using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Settings;

public sealed class EnvironmentCloudApiKeyProvider : ICloudApiKeyProvider
{
    public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? apiKey = ResolveApiKey(providerKey);
        return Task.FromResult(string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim());
    }

    private static string? ResolveApiKey(string providerKey)
    {
        ArgumentNullException.ThrowIfNull(providerKey);

        if (string.Equals(providerKey, "deepl", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable("DEEPL_AUTH_KEY") ??
                   Environment.GetEnvironmentVariable("DEEPL_API_KEY") ??
                   Environment.GetEnvironmentVariable("TRACKDUB_DEEPL_API_KEY");
        }

        if (string.Equals(providerKey, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
                   Environment.GetEnvironmentVariable("TRACKDUB_OPENAI_API_KEY");
        }

        if (string.Equals(providerKey, "gemini", StringComparison.OrdinalIgnoreCase))
        {
            // Gemini API (Google AI Studio) and Google Cloud are separate credential namespaces.
            // GOOGLE_API_KEY is intentionally excluded here to avoid accidentally using a
            // Google Cloud service key for Gemini API calls (different auth endpoint, different perms).
            return Environment.GetEnvironmentVariable("GEMINI_API_KEY") ??
                   Environment.GetEnvironmentVariable("TRACKDUB_GEMINI_API_KEY");
        }

        if (string.Equals(providerKey, "google", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ??
                   Environment.GetEnvironmentVariable("TRACKDUB_GOOGLE_API_KEY");
        }

        if (string.Equals(providerKey, "elevenlabs", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") ??
                   Environment.GetEnvironmentVariable("TRACKDUB_ELEVENLABS_API_KEY");
        }

        string normalized = providerKey
            .Trim()
            .Replace('-', '_')
            .ToUpperInvariant();
        return Environment.GetEnvironmentVariable($"TRACKDUB_{normalized}_API_KEY");
    }
}
