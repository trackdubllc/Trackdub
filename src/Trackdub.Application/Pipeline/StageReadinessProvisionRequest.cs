using Trackdub.Application.Transcripts;
using Trackdub.Domain;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Inputs for provisioning models required before a pipeline stage can run.
/// </summary>
public sealed record StageReadinessProvisionRequest(
    string StageKey,
    TranscriptWorkspace Workspace,
    RuntimeModelSelections Selections,
    RuntimeModelSetupCallbacks Callbacks,
    string? SourceLanguageCode,
    string? TargetLanguageCode,
    string? LipSyncModelAlias,
    string? LipSynthesisModelAlias,
    bool RequiresVoiceClone);
