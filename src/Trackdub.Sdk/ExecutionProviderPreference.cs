namespace Trackdub.Sdk;

/// <summary>
/// Specifies the preferred ONNX Runtime execution provider for inference operations.
/// </summary>
public enum ExecutionProviderPreference
{
    /// <summary>Let the runtime planner choose the best available provider (Windows: catalog EPs via Windows ML, then DirectML legacy fallback).</summary>
    Auto = 0,

    /// <summary>Force CPU-only inference.</summary>
    Cpu = 1,

    /// <summary>Request legacy Windows GPU acceleration via DirectML (Windows ML packaged route).</summary>
    DirectML = 2,

    /// <summary>Use NVIDIA CUDA GPU acceleration.</summary>
    Cuda = 3
}
