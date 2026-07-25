using Trackdub.Contracts.Licensing;
using Trackdub.Domain;

namespace Trackdub.TestDoubles;

/// <summary>
/// A no-op <see cref="IModelCacheRegistrar"/> for use in tests that do not need to verify
/// cache registration behaviour.
/// </summary>
public sealed class FakeModelCacheRegistrar : IModelCacheRegistrar
{
    public Task RegisterAsync(
        LocalModelCacheRecord record,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
