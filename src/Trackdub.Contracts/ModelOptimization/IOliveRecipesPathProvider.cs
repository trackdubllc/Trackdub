namespace Trackdub.Contracts.ModelOptimization;

public interface IOliveRecipesPathProvider
{
    /// <summary>
    /// Root folder containing Olive recipe JSON trees (e.g. openai-whisper-tiny/, microsoft-Phi-3.5-mini-instruct/).
    /// Returns null when no recipes root is configured or the path does not exist.
    /// </summary>
    string? TryGetRecipesRoot();
}
