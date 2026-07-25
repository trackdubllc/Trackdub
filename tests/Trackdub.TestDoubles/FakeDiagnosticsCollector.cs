using Trackdub.Contracts.Diagnostics;

namespace Trackdub.TestDoubles;

/// <summary>
/// Fake implementation of <see cref="IDiagnosticsCollector"/> that returns a configurable
/// <see cref="DiagnosticsSnapshot"/> for use in unit tests.
/// </summary>
public sealed class FakeDiagnosticsCollector : IDiagnosticsCollector
{
    public static readonly DiagnosticsSnapshot DefaultSnapshot = new DiagnosticsSnapshot(
        OsVersion: "Windows 11 (test)",
        Architecture: "X64",
        GpuDescription: "Test GPU",
        DirectMlAvailable: true,
        OnnxRuntimeVersion: "1.19.0",
        WindowsAppSdkVersion: "1.5.0",
        DbSchemaVersion: 20,
        ModelCacheEntries: [],
        LogFilePaths: [],
        CollectedAtUtc: DateTimeOffset.UnixEpoch);

    public DiagnosticsSnapshot Snapshot { get; set; } = DefaultSnapshot;

    /// <inheritdoc />
    public Task<DiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot);
}
