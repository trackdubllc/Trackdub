using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeLocalAssistant : ILocalAssistant
{
    public bool IsAvailable { get; set; }

    public LocalAssistantReply Reply { get; set; } = new(string.Empty, WasAnswered: false, FallbackReason: "Not configured.");

    public Task<LocalAssistantReply> AskAsync(
        LocalAssistantRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult(Reply);
}
