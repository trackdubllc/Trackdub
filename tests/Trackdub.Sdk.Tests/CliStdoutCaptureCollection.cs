namespace Trackdub.Sdk.Tests;

/// <summary>
/// Serializes tests that redirect <see cref="Console.Out"/> to avoid cross-test stdout races.
/// </summary>
[CollectionDefinition(nameof(CliStdoutCaptureCollection), DisableParallelization = true)]
public sealed class CliStdoutCaptureCollection;
