using Trackdub.Application.Dubbing;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Sdk;

/// <summary>
/// Obsolete: Use <see cref="IDubbingPipelineEngine"/> directly.
/// This interface was an unnecessary abstraction that duplicated the pipeline engine contract.
/// </summary>
[Obsolete("Use Trackdub.Application.Dubbing.IDubbingPipelineEngine directly. This interface will be removed in a future version.", error: true)]
internal interface IDubbingEngine : IDubbingPipelineEngine
{
}
