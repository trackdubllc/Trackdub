using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class EpContextArtifactStampTests
{
    [Fact]
    public void Stamp_records_runtime_lineage_so_a_trt_rtx_runtime_bump_invalidates_the_artifact()
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-epc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Join(directory, "model.onnx");

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

    [Fact]
    public void Legacy_stamp_rejects_missing_required_artifact_external_initializers()
    {
        var legacyStamp = new EpContextArtifact.Stamp(
            SchemaVersion: 1,
            SourceFileName: "model.onnx",
            SourceLengthBytes: 4,
            SourceLastWriteUtcTicks: DateTimeOffset.UtcNow.Ticks,
            SourceSha256: null,
            GpuArchitecture: "Ada",
            DriverVersion: "560.35.03",
            TrtRtxEpVersion: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        Assert.False(legacyStamp.MatchesArtifactExternalInitializers(null, sidecarRequired: true));
    }

    [Fact]
    public void Stamp_invalidates_when_external_model_data_is_replaced()
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-epc-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Join(directory, "model.onnx");
        string externalDataPath = EpContextArtifact.GetSourceExternalDataPath(sourcePath);
        try
        {
            File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
            File.WriteAllBytes(externalDataPath, [5, 6, 7, 8]);
            File.WriteAllBytes(EpContextArtifact.GetEpContextPath(sourcePath), [9, 10, 11, 12]);

            EpContextArtifact.Stamp stamp = EpContextArtifact.CreateStamp(
                sourcePath, new FileInfo(sourcePath), sourceSha256: null,
                gpuArchitecture: "Ada", driverVersion: "560.35.03");
            EpContextArtifact.WriteStamp(sourcePath, stamp);
            Assert.Equal(
                EpContextArtifact.GetEpContextPath(sourcePath),
                EpContextArtifact.TryResolveValidLoadPath(sourcePath, stamp.EnvironmentFingerprint));

            File.WriteAllBytes(externalDataPath, [5, 6, 7, 9]);
            File.SetLastWriteTimeUtc(
                externalDataPath,
                new DateTime(stamp.ExternalDataLastWriteUtcTicks!.Value, DateTimeKind.Utc).AddDays(1));
            Assert.Null(EpContextArtifact.TryResolveValidLoadPath(sourcePath, stamp.EnvironmentFingerprint));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Stamp_invalidates_when_artifact_external_initializers_change()
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-epc-sidecar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Join(directory, "model.onnx");
        string epContextPath = EpContextArtifact.GetEpContextPath(sourcePath);
        string sidecarPath = EpContextArtifact.GetArtifactExternalInitializersPath(epContextPath);
        try
        {
            File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
            File.WriteAllBytes(epContextPath, [5, 6, 7, 8]);
            File.WriteAllBytes(sidecarPath, [9, 10, 11, 12]);

            EpContextArtifact.Stamp stamp = EpContextArtifact.CreateStamp(
                sourcePath, new FileInfo(sourcePath), sourceSha256: null,
                gpuArchitecture: "Ada", driverVersion: "560.35.03");
            EpContextArtifact.WriteStamp(sourcePath, stamp);
            Assert.Equal(
                epContextPath,
                EpContextArtifact.TryResolveValidLoadPath(sourcePath, stamp.EnvironmentFingerprint));

            File.WriteAllBytes(sidecarPath, [9, 10, 11, 13]);
            File.SetLastWriteTimeUtc(
                sidecarPath,
                new DateTime(stamp.ArtifactExternalInitializersLastWriteUtcTicks!.Value, DateTimeKind.Utc).AddDays(1));
            Assert.Null(EpContextArtifact.TryResolveValidLoadPath(sourcePath, stamp.EnvironmentFingerprint));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Optimum_onnx_data_sidecar_counts_as_external_data()
    {
        // Optimum exports name the weights sidecar <model>.onnx_data; replacing it must invalidate the
        // artifact, and its bytes decide whether the engine can be embedded under protobuf's 2 GB limit.
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-epc-onnxdata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Join(directory, "decoder_model.onnx");
        try
        {
            File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
            File.WriteAllBytes(sourcePath + "_data", [5, 6, 7, 8, 9, 10]);

            Assert.Equal(sourcePath + "_data", EpContextArtifact.GetSourceExternalDataPath(sourcePath));
            Assert.Equal(10, EpContextArtifact.GetSourceTotalBytes(sourcePath));

            File.WriteAllBytes(EpContextArtifact.GetEpContextPath(sourcePath), [11]);
            EpContextArtifact.Stamp stamp = EpContextArtifact.CreateStamp(
                sourcePath, new FileInfo(sourcePath), sourceSha256: null,
                gpuArchitecture: "Blackwell", driverVersion: "32.0.16.1714");
            Assert.Equal(6, stamp.ExternalDataLengthBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Published_artifact_requires_its_engine_files_and_replaces_the_previous_set()
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-epc-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Join(directory, "encoder_model.onnx");
        string epContextName = Path.GetFileName(EpContextArtifact.GetEpContextPath(sourcePath));
        try
        {
            File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);

            IReadOnlyList<EpContextArtifact.ArtifactFile> first = Publish(directory, sourcePath, epContextName, "old.engine", [7, 7]);
            EpContextArtifact.ArtifactFile engine = Assert.Single(first);
            Assert.Equal("old.engine", engine.Name);
            Assert.Equal(2, engine.LengthBytes);
            Assert.Equal(File.GetLastWriteTimeUtc(Path.Join(directory, engine.Name)).Ticks, engine.LastWriteUtcTicks);
            string fingerprint = WriteStamp(sourcePath, first);
            Assert.Equal(
                EpContextArtifact.GetEpContextPath(sourcePath),
                EpContextArtifact.TryResolveValidLoadPath(sourcePath, fingerprint));

            // A truncated or missing engine invalidates the artifact even though the stamp and model remain.
            File.WriteAllBytes(Path.Join(directory, "old.engine"), [7]);
            Assert.Null(EpContextArtifact.TryResolveValidLoadPath(sourcePath, fingerprint));

            // Republishing drops the previous stamp and the engine files it recorded.
            IReadOnlyList<EpContextArtifact.ArtifactFile> second = Publish(directory, sourcePath, epContextName, "new.engine", [8, 8, 8]);
            Assert.False(File.Exists(Path.Join(directory, "old.engine")));
            Assert.False(File.Exists(EpContextArtifact.GetStampPath(sourcePath)));
            fingerprint = WriteStamp(sourcePath, second);
            Assert.Equal(
                EpContextArtifact.GetEpContextPath(sourcePath),
                EpContextArtifact.TryResolveValidLoadPath(sourcePath, fingerprint));

            EpContextArtifact.DeleteArtifact(sourcePath);
            Assert.False(File.Exists(Path.Join(directory, "new.engine")));
            Assert.False(File.Exists(EpContextArtifact.GetEpContextPath(sourcePath)));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static IReadOnlyList<EpContextArtifact.ArtifactFile> Publish(
            string directory, string sourcePath, string epContextName, string engineName, byte[] engineBytes)
        {
            string staging = Path.Join(directory, ".epc-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            File.WriteAllBytes(Path.Join(staging, epContextName), [5, 6]);
            File.WriteAllBytes(Path.Join(staging, engineName), engineBytes);
            IReadOnlyList<EpContextArtifact.ArtifactFile> files = EpContextArtifact.PublishStagedArtifact(staging, sourcePath);
            Directory.Delete(staging, recursive: true);
            return files;
        }

        static string WriteStamp(string sourcePath, IReadOnlyList<EpContextArtifact.ArtifactFile> files)
        {
            EpContextArtifact.Stamp stamp = EpContextArtifact.CreateStamp(
                sourcePath, new FileInfo(sourcePath), sourceSha256: null,
                gpuArchitecture: "Blackwell", driverVersion: "32.0.16.1714", files);
            EpContextArtifact.WriteStamp(sourcePath, stamp);
            return stamp.EnvironmentFingerprint;
        }
    }
}
