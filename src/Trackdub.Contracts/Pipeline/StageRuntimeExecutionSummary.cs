namespace Trackdub.Contracts.Pipeline;

public sealed record StageRuntimeExecutionSummary(
    string RequestedProvider,
    string SelectedProvider,
    string? ModelId = null,
    string? ModelAlias = null,
    string? ModelVariant = null,
    string? BootstrapDetail = null);

public interface IStageRuntimeExecutionReporter
{
    StageRuntimeExecutionSummary? LastExecutionSummary { get; }
}
