using Trackdub.Domain;

namespace Trackdub.Contracts.Licensing;

/// <summary>
/// Registers locally available model files so runtime planning can use them.
/// </summary>
public interface IModelCacheRegistrar
{
    Task RegisterAsync(
        LocalModelCacheRecord record,
        CancellationToken cancellationToken = default);
}
