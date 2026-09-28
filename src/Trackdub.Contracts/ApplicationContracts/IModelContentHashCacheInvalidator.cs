namespace Trackdub.Contracts.ApplicationContracts;

/// <summary>Invalidates cached model-content fingerprints after an installed file is replaced.</summary>
public interface IModelContentHashCacheInvalidator
{
    /// <summary>Invalidates the cached fingerprint for <paramref name="modelPath"/>.</summary>
    void Invalidate(string modelPath);
}
