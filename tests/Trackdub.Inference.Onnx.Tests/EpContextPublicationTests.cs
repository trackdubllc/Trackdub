using Trackdub.Inference.Onnx.EpContext;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class EpContextPublicationTests : IDisposable
{
    private readonly string directory = Path.Join(Path.GetTempPath(), "trackdub-publication-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Join(directory, "model.onnx");

    public EpContextPublicationTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Source, [1]);
    }

    [Fact]
    public void Custom_output_preserves_canonical_artifact_and_publishes_at_requested_path()
    {
        string canonical = EpContextArtifact.GetEpContextPath(Source);
        File.WriteAllBytes(canonical, [9]);
        string outputDirectory = Path.Join(directory, "custom");
        Directory.CreateDirectory(outputDirectory);
        string output = Path.Join(outputDirectory, "requested.onnx");
        string staging = Stage(output, "engine.bin", [2, 3]);

        EpContextArtifact.PublishStagedArtifact(staging, Source, output,
            files => Stamp(files, output));

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(canonical));
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Join(outputDirectory, "engine.bin")));
        Assert.True(File.Exists(Path.ChangeExtension(output, ".stamp.json")));
        Assert.False(File.Exists(Path.Join(directory, "engine.bin")));
    }

    [Fact]
    public void Failed_stamp_rolls_back_new_engines_and_restores_previous_artifact()
    {
        string output = EpContextArtifact.GetEpContextPath(Source);
        EpContextArtifact.PublishStagedArtifact(Stage(output, "old.engine", [1, 2]), Source,
            createStamp: files => Stamp(files));
        byte[] previousStamp = File.ReadAllBytes(EpContextArtifact.GetStampPath(Source));
        // Change source identity so the validity recheck does not reuse the old artifact.
        File.WriteAllBytes(Source, [1, 2]);

        Assert.Throws<IOException>(() => EpContextArtifact.PublishStagedArtifact(
            Stage(output, "new.engine", [3, 4]), Source,
            createStamp: files => files.Count == 0 ? Stamp(files) : throw new IOException("stamp failure")));

        Assert.False(File.Exists(Path.Join(directory, "new.engine")));
        Assert.True(File.Exists(Path.Join(directory, "old.engine")));
        Assert.True(File.Exists(output));
        Assert.Equal(previousStamp, File.ReadAllBytes(EpContextArtifact.GetStampPath(Source)));
    }

    [Fact]
    public void Failed_model_move_removes_engines_even_before_stamp_exists()
    {
        string output = EpContextArtifact.GetEpContextPath(Source);
        string staging = Stage(output, "new.engine", [3, 4]);
        Directory.CreateDirectory(output); // Force the final model move to fail after the engine move.

        Assert.ThrowsAny<IOException>(() => EpContextArtifact.PublishStagedArtifact(staging, Source));

        Assert.False(File.Exists(Path.Join(directory, "new.engine")));
        Assert.False(File.Exists(EpContextArtifact.GetStampPath(Source)));
    }

    [Fact]
    public void Same_length_engine_replacement_invalidates_stamp()
    {
        string output = EpContextArtifact.GetEpContextPath(Source);
        EpContextArtifact.PublishStagedArtifact(Stage(output, "engine.bin", [1, 2]), Source,
            createStamp: files => Stamp(files));
        string fingerprint = Stamp([]).EnvironmentFingerprint;
        Assert.NotNull(EpContextArtifact.TryResolveValidLoadPath(Source, fingerprint));
        string engine = Path.Join(directory, "engine.bin");
        long originalTicks = File.GetLastWriteTimeUtc(engine).Ticks;
        File.WriteAllBytes(engine, [3, 4]);
        File.SetLastWriteTimeUtc(engine, new DateTime(originalTicks, DateTimeKind.Utc).AddSeconds(2));

        Assert.Null(EpContextArtifact.TryResolveValidLoadPath(Source, fingerprint));
    }

    [Fact]
    public async Task Overlapping_publishers_reuse_complete_artifact_under_exclusive_lock()
    {
        string output = EpContextArtifact.GetEpContextPath(Source);
        string firstStage = Stage(output, "first.engine", [1, 2]);
        string secondStage = Stage(output, "second.engine", [3, 4]);
        using var inStamp = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<IReadOnlyList<EpContextArtifact.ArtifactFile>> first = Task.Run(() =>
            EpContextArtifact.PublishStagedArtifact(firstStage, Source, createStamp: files =>
            {
                if (files.Count > 0)
                {
                    inStamp.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) throw new TimeoutException();
                }
                return Stamp(files);
            }));
        Task<IReadOnlyList<EpContextArtifact.ArtifactFile>>? second = null;
        try
        {
            Assert.True(inStamp.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Throws<IOException>(() => new FileStream(output + ".publish.lock", FileMode.Open,
                FileAccess.ReadWrite, FileShare.None));
            second = Task.Run(() => EpContextArtifact.PublishStagedArtifact(secondStage, Source,
                createStamp: files => Stamp(files)));
        }
        finally
        {
            release.Set();
            await first;
        }

        Assert.Equal(await first, await second!);
        Assert.True(File.Exists(Path.Join(directory, "first.engine")));
        Assert.False(File.Exists(Path.Join(directory, "second.engine")));
        Assert.NotNull(EpContextArtifact.TryResolveValidLoadPath(Source, Stamp([]).EnvironmentFingerprint));
    }

    [Theory]
    [InlineData("out of memory", false)]
    [InlineData("TensorRT engine build failed", false)]
    [InlineData("EPContext failed to write artifact", true)]
    public void Native_build_failure_is_not_retried_as_capture_failure(string message, bool retry)
    {
        Assert.Equal(retry, EpContextCapture.IsCaptureWriteFailure(message));
    }

    private EpContextArtifact.Stamp Stamp(IReadOnlyList<EpContextArtifact.ArtifactFile> files, string? output = null) =>
        EpContextArtifact.CreateStamp(Source, new FileInfo(Source), null, "test", "driver", files, output);

    private string Stage(string output, string engine, byte[] bytes)
    {
        string staging = Path.Join(directory, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Join(staging, Path.GetFileName(output)), [5]);
        File.WriteAllBytes(Path.Join(staging, engine), bytes);
        return staging;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
