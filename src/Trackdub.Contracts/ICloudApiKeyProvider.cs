namespace Trackdub.Contracts;

public interface ICloudApiKeyProvider
{
    Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken);
}
