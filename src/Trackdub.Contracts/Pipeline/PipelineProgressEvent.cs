namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Identifies the kind of progress event emitted during pipeline execution.
/// </summary>
public enum PipelineProgressEventKind
{
    /// <summary>A pipeline stage has started execution.</summary>
    Started = 0,

    /// <summary>A pipeline stage reports incremental progress.</summary>
    Progress = 1,

    /// <summary>A pipeline stage completed successfully.</summary>
    Completed = 2,

    /// <summary>A pipeline stage failed with an error.</summary>
    Failed = 3,

    /// <summary>A pipeline stage was skipped.</summary>
    Skipped = 4
}

/// <summary>
/// A structured progress event emitted during pipeline execution.
/// </summary>
public sealed record PipelineProgressEvent
{
    public PipelineProgressEvent(
        string StageName,
        PipelineProgressEventKind EventKind,
        double Percentage,
        string? Message,
        TimeSpan ElapsedDuration)
        : this(
            StageName,
            EventKind,
            Percentage,
            Message,
            ElapsedDuration,
            StageKey: null,
            Phase: null,
            CompletedUnits: null,
            TotalUnits: null,
            CurrentItemLabel: null)
    {
    }

    public PipelineProgressEvent(
        string StageName,
        PipelineProgressEventKind EventKind,
        double? PercentComplete = null,
        string? Message = null,
        TimeSpan ElapsedDuration = default,
        string? StageKey = null,
        string? Phase = null,
        int? CompletedUnits = null,
        int? TotalUnits = null,
        string? CurrentItemLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StageName);

        this.StageName = StageName;
        this.EventKind = EventKind;
        this.PercentComplete = PercentComplete is double value
            ? Math.Clamp(value, 0d, 100d)
            : null;
        this.Message = Message;
        this.ElapsedDuration = ElapsedDuration;
        this.StageKey = string.IsNullOrWhiteSpace(StageKey) ? StageName : StageKey;
        this.Phase = Phase;
        this.CompletedUnits = CompletedUnits;
        this.TotalUnits = TotalUnits;
        this.CurrentItemLabel = CurrentItemLabel;
    }

    /// <summary>The user-facing name of the pipeline stage.</summary>
    public string StageName { get; init; }

    /// <summary>The stable pipeline stage key when it differs from <see cref="StageName"/>.</summary>
    public string StageKey { get; init; }

    /// <summary>The kind of progress event.</summary>
    public PipelineProgressEventKind EventKind { get; init; }

    /// <summary>Progress percentage from 0 to 100, or null when the workflow cannot know it honestly.</summary>
    public double? PercentComplete { get; init; }

    /// <summary>Compatibility value for existing callers that expect a non-null percentage.</summary>
    public double Percentage => PercentComplete ?? 0d;

    /// <summary>Optional machine-readable or concise phase name.</summary>
    public string? Phase { get; init; }

    /// <summary>Optional completed unit count for determinate work.</summary>
    public int? CompletedUnits { get; init; }

    /// <summary>Optional total unit count for determinate work.</summary>
    public int? TotalUnits { get; init; }

    /// <summary>Optional label for the current item being processed.</summary>
    public string? CurrentItemLabel { get; init; }

    /// <summary>Optional human-readable message providing additional context.</summary>
    public string? Message { get; init; }

    /// <summary>Elapsed time for terminal events.</summary>
    public TimeSpan ElapsedDuration { get; init; }
}
