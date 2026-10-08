namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Public entry point for the host's largest dedicated accelerator memory, using the same
/// probe that sizes the session pool admission budget. Returns 0 when unknown.
/// </summary>
public static class AcceleratorVramDetector
{
    public static long DetectMaxDedicatedVramMb() => InferenceSessionPool.DetectMaxAcceleratorVramMb();
}
