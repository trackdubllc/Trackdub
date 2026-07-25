namespace Trackdub.Inference.Runtime.ModelManifest;

public sealed class ModelManifestValidationException(string message) : InvalidOperationException(message);
