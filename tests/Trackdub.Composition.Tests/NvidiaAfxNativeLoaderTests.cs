using Trackdub.Composition.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxNativeLoaderTests
{
    private static readonly string Sdk = Path.Join(Path.GetTempPath(), "afx-sdk-a");
    private static readonly string OtherSdk = Path.Join(Path.GetTempPath(), "afx-sdk-b");

    [Fact]
    public void EnsureSameRuntimeRoot_AllowsTheFirstLoad()
    {
        NvidiaAfxNativeLoader.EnsureSameRuntimeRoot(loadedFrom: null, requestedRoot: Sdk);
    }

    [Fact]
    public void EnsureSameRuntimeRoot_AllowsTheSameRoot_RegardlessOfSeparatorOrCase()
    {
        NvidiaAfxNativeLoader.EnsureSameRuntimeRoot(Sdk, Sdk + Path.DirectorySeparatorChar);
        NvidiaAfxNativeLoader.EnsureSameRuntimeRoot(Sdk, Sdk.ToUpperInvariant());
    }

    [Fact]
    public void EnsureSameRuntimeRoot_RefusesADifferentRuntime_AndAsksForARestart()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            NvidiaAfxNativeLoader.EnsureSameRuntimeRoot(Sdk, OtherSdk));

        Assert.Contains("Restart Trackdub", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Sdk, ex.Message, StringComparison.Ordinal);
        Assert.Contains(OtherSdk, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindConflictingModule_ReportsADifferentCopyThatIsAlreadyLoaded()
    {
        string dependency = Path.Join(Sdk, "bin", "external", "cuda", "bin", "cublas64_12.dll");
        string loadedElsewhere = Path.Join(OtherSdk, "cublas64_12.dll");

        string? conflict = NvidiaAfxNativeLoader.FindConflictingModule([dependency], [loadedElsewhere]);

        Assert.NotNull(conflict);
        Assert.Contains("cublas64_12.dll", conflict, StringComparison.Ordinal);
        Assert.Contains(loadedElsewhere, conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void FindConflictingModule_MatchesFileNamesWithoutRegardToCase()
    {
        string dependency = Path.Join(Sdk, "bin", "external", "nvtrt", "bin", "nvinfer_10.dll");

        Assert.NotNull(NvidiaAfxNativeLoader.FindConflictingModule(
            [dependency],
            [Path.Join(OtherSdk, "NVINFER_10.DLL")]));
    }

    [Fact]
    public void FindConflictingModule_IgnoresTheSameFileAlreadyLoadedFromTheSameFolder()
    {
        string dependency = Path.Join(Sdk, "bin", "external", "cuda", "bin", "cublas64_12.dll");

        Assert.Null(NvidiaAfxNativeLoader.FindConflictingModule([dependency], [dependency]));
    }

    [Fact]
    public void FindConflictingModule_IgnoresUnrelatedModules()
    {
        string dependency = Path.Join(Sdk, "bin", "external", "cuda", "bin", "cublas64_12.dll");

        Assert.Null(NvidiaAfxNativeLoader.FindConflictingModule(
            [dependency],
            [Path.Join(OtherSdk, "onnxruntime.dll"), Path.Join(OtherSdk, "kernel32.dll")]));
    }
}
