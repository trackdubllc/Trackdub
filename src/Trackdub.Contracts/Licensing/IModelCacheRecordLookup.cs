using Trackdub.Domain;

namespace Trackdub.Contracts.Licensing;

/// <summary>
/// Read-only access to locally registered model cache records for preflight/status checks.
/// </summary>
public interface IModelCacheRecordLookup
{
    LocalModelCacheRecord? Find(string modelId, string rootPath);
}
