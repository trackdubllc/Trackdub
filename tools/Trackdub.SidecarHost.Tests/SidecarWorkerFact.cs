using Xunit;

namespace Trackdub.SidecarHost.Tests;

/// <summary>
/// Opt-in gate for tests that spawn the REAL Chatterbox Python worker.
/// Default: skipped (no model stack, no weights). Set TRACKDUB_SIDECAR_TESTS=1 to run.
/// Mirrors the repo's RequiresBundledModelFact pattern: skip is honest, never fake-ready.
/// </summary>
public sealed class SidecarWorkerFactAttribute : FactAttribute
{
    public SidecarWorkerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRACKDUB_SIDECAR_TESTS") != "1")
        {
            Skip = "Set TRACKDUB_SIDECAR_TESTS=1 and run `uv sync --extra model` in workers/chatterbox to enable live sidecar tests.";
        }
    }
}
