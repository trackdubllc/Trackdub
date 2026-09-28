using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>Connects successful model-file replacement to pooled-session content identity.</summary>
public sealed class SessionPoolModelContentHashCacheInvalidator : IModelContentHashCacheInvalidator
{
    /// <inheritdoc />
    public void Invalidate(string modelPath) => SessionPoolKey.InvalidateModelContentHash(modelPath);
}
