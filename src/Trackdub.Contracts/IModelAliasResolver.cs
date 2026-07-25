namespace Trackdub.Contracts;

public interface IModelAliasResolver
{
    bool TryResolveModelId(string modelAlias, out string? modelId);
}
