using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class EpContextArtifactStampTests
{
    [Fact]
    public void Stamp_records_runtime_lineage_so_a_trt_rtx_runtime_bump_invalidates_the_artifact()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"trackdub-epc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "model.onnx");

        try
        {
            File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
            File.WriteAllBytes(EpContextArtifact.GetEpContextPath(sourcePath), [5, 6, 7, 8]);
            EpContextArtifact.Stamp stamp = EpContextArtifact.CreateStamp(
                sourcePath,
                new FileInfo(sourcePath),
                sourceSha256: null,
                gpuArchitecture: "Blackwell",
                driverVersion: "32.0.16.1714");
            EpContextArtifact.WriteStamp(sourcePath, stamp);

            Assert.Equal(TensorRtRtxProviderConstants.BundledFingerprintVersion, stamp.TrtRtxEpVersion);
            Assert.Equal(
                EpContextArtifact.GetEpContextPath(sourcePath),
                EpContextArtifact.TryResolveValidLoadPath(sourcePath, stamp.EnvironmentFingerprint));

            // Same EP ABI plugin version, different vendored TensorRT-RTX runtime → stale artifact.
            string otherRuntime =
                $"Blackwell|32.0.16.1714|{TensorRtRtxProviderConstants.BundledVersion}+trt-rtx-1.5.0";
            Assert.Null(EpContextArtifact.TryResolveValidLoadPath(sourcePath, otherRuntime));

            // Pre-upgrade stamps carried only the EP ABI version and must not match either.
            string legacy = $"Blackwell|32.0.16.1714|{TensorRtRtxProviderConstants.BundledVersion}";
            Assert.Null(EpContextArtifact.TryResolveValidLoadPath(sourcePath, legacy));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
