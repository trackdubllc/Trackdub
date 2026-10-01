using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Infrastructure.Tests.Components.NvidiaAfx;

public sealed class NvidiaAfxRuntimeLayoutTests
{
    [Fact]
    public void ResolveNativeLibraryPath_FindsMaxineSpelling()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string dll = Path.Join(tempRoot, "NVAudioEffects.dll");
            File.WriteAllBytes(dll, [0x00]);
            Assert.Equal(dll, NvidiaAfxRuntimeLayout.ResolveNativeLibraryPath(tempRoot));
            Assert.True(NvidiaAfxRuntimeLayout.HasNativeLibrary(tempRoot));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveModelFile_PrefersFeatureArchLayout()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string modelDir = Path.Join(tempRoot, "features", "nvafxdenoiser", "models", "ada");
            Directory.CreateDirectory(modelDir);
            string modelPath = Path.Join(modelDir, "denoiser_48k.trtpkg");
            File.WriteAllText(modelPath, "stub");

            // Legacy path should lose to Maxine layout when both could exist; only Maxine exists here.
            string? resolved = NvidiaAfxRuntimeLayout.ResolveModelFile(
                tempRoot,
                "nvafxdenoiser",
                "denoiser_48k",
                "ada");

            Assert.Equal(modelPath, resolved);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveModelFile_FallsBackToLegacyFlatModels()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            Directory.CreateDirectory(Path.Join(tempRoot, "models"));
            string legacy = Path.Join(tempRoot, "models", "dereverb_denoiser_48k.nvam");
            File.WriteAllText(legacy, "stub");

            string? resolved = NvidiaAfxRuntimeLayout.ResolveModelFile(
                tempRoot,
                "nvafxdereverbdenoiser",
                "dereverb_denoiser_48k",
                "ada");

            Assert.Equal(legacy, resolved);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void FeatureNativeLibraryRelativePath_MatchesMaxineDocs()
    {
        Assert.Equal(
            Path.Join("features", "nvafxstudiovoicelowlatency", "bin", "nvafxstudiovoicelowlatency.dll"),
            NvidiaAfxRuntimeLayout.FeatureNativeLibraryRelativePath("nvafxstudiovoicelowlatency"));
    }

    [Fact]
    public void ResolveFeatureNativeLibraryPath_FindsFeaturesBinLayout()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-feature-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string binDir = Path.Join(tempRoot, "features", "nvafxdenoiser", "bin");
            Directory.CreateDirectory(binDir);
            string dll = Path.Join(binDir, "nvafxdenoiser.dll");
            File.WriteAllBytes(dll, [0x00]);

            Assert.True(NvidiaAfxRuntimeLayout.HasFeaturesDirectory(tempRoot));
            Assert.Equal(dll, NvidiaAfxRuntimeLayout.ResolveFeatureNativeLibraryPath(tempRoot, "nvafxdenoiser"));
            Assert.True(NvidiaAfxRuntimeLayout.HasRequiredFeatureLibraries(tempRoot, ["nvafxdenoiser"]));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
