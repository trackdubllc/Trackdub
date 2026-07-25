namespace Trackdub.Contracts;

public interface ILocalAssistant
{
    bool IsAvailable { get; }

    Task<LocalAssistantReply> AskAsync(
        LocalAssistantRequest request,
        CancellationToken cancellationToken = default);
}

public enum LocalAssistantScope
{
    Onboarding,
    StarterPackExplain,
    StarterPackAudit,
    GeneralHelp
}

public sealed record LocalAssistantRequest(
    string UserMessage,
    LocalAssistantScope Scope,
    string? ContextJson = null);

public sealed record LocalAssistantReply(
    string Text,
    bool WasAnswered,
    string? FallbackReason = null,
    IReadOnlyList<Trackdub.Contracts.StarterPacks.StarterPackPatchOperation>? ProposedPatches = null);
