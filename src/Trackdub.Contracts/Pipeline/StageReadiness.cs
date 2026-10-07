namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Readiness status for a single pipeline stage, as evaluated against a
/// specific set of runtime model selections.
/// </summary>
public sealed record StageReadiness(
    /// <summary>Stage name (StageNames.* constant).</summary>
    string StageName,

    /// <summary>Current readiness state.</summary>
    ReadinessState Status,

    /// <summary>Human-readable detail for display in the readiness panel. Null when Status is Ready/Satisfied.</summary>
    string? Detail,

    /// <summary>Model ID implicated by this status (download/import target). Null for non-model states.</summary>
    string? ModelId,

    /// <summary>Model alias implicated by this status. Null when not applicable.</summary>
    string? ModelAlias,

    /// <summary>
    /// Opaque resolve-action code for the panel "Resolve" button.
    /// One of: null | "download" | "import" | "install-runtime" | "review-license" | "set-api-key" | "grant-consent" | "grant-egress-consent"
    /// </summary>
    string? ResolveAction);
