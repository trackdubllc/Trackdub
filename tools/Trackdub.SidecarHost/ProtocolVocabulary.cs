namespace Trackdub.SidecarHost;

/// <summary>
/// Closed status/reason vocabularies from workers/PROTOCOL.md ("closed sets —
/// do not invent more"). The Rust supervisor rejects out-of-set values at
/// deserialization; the C# host validates after deserialization, so an
/// unknown status or reason cannot cross the host boundary as if it were
/// a valid response.
/// </summary>
public static class ProtocolVocabulary
{
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(["alive", "loaded", "ok", "error"]);

    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>(
    [
        "invalid-json", "unknown-op", "bad-plan", "bad-inputs",
        "dependency-missing", "model-not-found", "load-failed",
        "no-model-loaded", "infer-failed", "load-not-implemented",
    ]);

    /// <summary>Throws <see cref="InvalidOperationException"/> when the
    /// response's status or reason is outside the protocol's closed sets.</summary>
    public static void Validate(SidecarResponse response)
    {
        if (!Statuses.Contains(response.Status))
        {
            throw new InvalidOperationException(
                $"worker status {response.Status} is outside the protocol's closed status set");
        }
        if (response.Reason is not null && !Reasons.Contains(response.Reason))
        {
            throw new InvalidOperationException(
                $"worker reason {response.Reason} is outside the protocol's closed reason set");
        }
    }
}
