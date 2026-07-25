namespace Trackdub.Contracts.Transcripts;

/// <summary>Character offset highlight within translated segment text.</summary>
public readonly record struct GlossaryTextHighlightSpan(int Start, int Length);
