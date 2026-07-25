namespace Trackdub.Sdk.Tests;

/// <summary>
/// Non-parallel collection for tests that mutate process-global <c>TRACKDUB_*</c>
/// environment variables. xUnit runs test classes in parallel by default; disabling
/// parallelization here prevents this class from racing other tests that read or
/// write the same process env while it is being touched.
/// </summary>
[CollectionDefinition("StorageEnvCollection", DisableParallelization = true)]
public sealed class StorageEnvCollectionDefinition
{
}
