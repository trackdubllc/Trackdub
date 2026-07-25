namespace Trackdub.Sdk;

/// <summary>
/// Configuration options controlling batch processing behavior.
/// </summary>
public sealed record BatchOptions
{
    /// <summary>When true, continue processing after a failure.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>Output root directory. When null, output is adjacent to each source file.</summary>
    public string? OutputRoot { get; init; }
}
