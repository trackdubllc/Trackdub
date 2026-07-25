namespace Trackdub.Contracts.Pipeline;

public interface ITtsEngine
{
    Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional capability mixed into <see cref="ITtsEngine"/> implementations that can
/// report the runtime execution summary alongside the synthesis result. Returning the
/// summary from the call avoids the cross-call mutable state race that occurs when
/// multiple parallel synthesis tasks share a single engine instance.
/// </summary>
public interface ITtsEngineWithExecutionSummary
{
    Task<(TtsSynthesisResult Result, StageRuntimeExecutionSummary? Summary)> SynthesizeWithSummaryAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken);
}
