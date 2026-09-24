using System.Runtime.CompilerServices;

namespace Trackdub.TestDoubles;

/// <summary>
/// Marks an xUnit test that needs a validated model staging directory produced by a
/// <c>tools/olive</c> validation script (for example <c>build/whisper-tiny-onnx-trtrtx-validated</c>).
/// If the directory is missing the test is skipped, so <c>dotnet test</c> works cleanly on
/// machines (and CI agents) that have not run the hardware validation.
/// </summary>
/// <remarks>
/// The attribute evaluates at test-discovery time via <see cref="FactAttribute.Skip"/>,
/// so missing staging directories never touch the engine code paths under test.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresStagedModelFactAttribute : FactAttribute
{
    public RequiresStagedModelFactAttribute(
        string relativePath,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        Skip = StagedModelSkipResolver.Resolve(relativePath);
    }
}

internal static class StagedModelSkipResolver
{
    internal static string? Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var repoRoot = TestRepoRootResolver.TryFindRepoRoot();
        if (repoRoot is null)
        {
            return "Unable to locate Trackdub.slnx from the test runner base directory; skipping staged-model test.";
        }

        var full = Path.GetFullPath(Path.Combine(repoRoot, relativePath));
        return Directory.Exists(full)
            ? null
            : $"Required staged model directory not present at {relativePath}. Run the tools/olive validation script for this model to stage it.";
    }
}
