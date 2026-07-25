namespace Trackdub.Infrastructure.Tests;

/// <summary>
/// Non-parallel collection for Hugging Face download tests that mutate process-global
/// environment variables (PATH, TRACKDUB_HF_*, HF_HUB_*). Prevents races with other
/// test classes that read or write the same process env.
/// </summary>
[CollectionDefinition(nameof(HuggingFaceEnvCollection), DisableParallelization = true)]
public sealed class HuggingFaceEnvCollection
{
}
