namespace Trackdub.Contracts.Benchmarking;

/// <summary>
/// Reports dedicated GPU memory allocated by this process.
/// </summary>
/// <remarks>
/// <para>
/// This is process-isolated usage of the adapter's own memory, which is the attribution
/// <see cref="IAvailableVramReader"/> cannot provide: DXGI's <c>QueryVideoMemoryInfo</c> reports
/// adapter-wide budget minus usage, so its reading also moves with every other process on the
/// same GPU. On Windows this reader instead uses the per-process "GPU Process Memory"
/// performance counter set, whose instance names are keyed by process id.
/// </para>
/// <para>
/// The two ports answer different questions and are not interchangeable: use this one to
/// attribute bytes to a stage or to bound one process's footprint, and
/// <see cref="IAvailableVramReader"/> to detect adapter-wide pressure. Returns null where the
/// platform or driver cannot report it.
/// </para>
/// <para>
/// The reading is also available to the shared ONNX session pool for telemetry. Because this
/// interface reports one process-wide total rather than per-adapter values, the pool does not
/// charge it to an individual device; accelerator admission remains reservation-only. See
/// <c>SharedPoolOptions</c> in <c>Trackdub.Inference.Onnx</c>.
/// </para>
/// </remarks>
public interface IProcessGpuMemoryReader
{
    /// <summary>
    /// Dedicated GPU memory currently allocated by this process, in bytes, or null when this
    /// platform or driver cannot report it.
    /// </summary>
    long? ReadDedicatedGpuMemoryBytes();

    /// <summary>Why the reading is unavailable, surfaced verbatim in benchmark evidence.</summary>
    string UnavailableReason { get; }
}
