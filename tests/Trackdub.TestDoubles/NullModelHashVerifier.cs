using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.TestDoubles;

/// <summary>
/// A no-op hash verifier for tests that exercise download orchestration without real model files.
/// Reports verification as skipped (valid but not actually checked).
/// </summary>
public sealed class NullModelHashVerifier : IModelHashVerifier
{
    public Task<HashVerificationResult> VerifyAsync(
        ModelManifest manifest,
        string filePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new HashVerificationResult(true, false, null, null, "Hash verification bypassed in test."));

    public Task<HashVerificationResult> VerifyAsync(
        string? expectedSha256,
        string filePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new HashVerificationResult(true, false, null, null, "Hash verification bypassed in test."));
}
