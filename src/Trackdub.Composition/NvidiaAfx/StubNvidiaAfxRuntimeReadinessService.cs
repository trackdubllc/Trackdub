using Trackdub.Contracts;

namespace Trackdub.Composition.NvidiaAfx;

/// <summary>
/// DI-registered AFX readiness while <see cref="NvidiaAfxIntegration.IsStubbed"/> is true.
/// Always reports not ready so settings/UI can discover AFX without fake readiness.
/// </summary>
public sealed class StubNvidiaAfxRuntimeReadinessService : INvidiaAfxRuntimeReadinessService
{
    public NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile) =>
        new(
            IsReady: false,
            StatusLabel: NvidiaAfxIntegration.StubStatusLabel,
            RuntimeRoot: null,
            FailureReason: NvidiaAfxIntegration.StubReason);
}
