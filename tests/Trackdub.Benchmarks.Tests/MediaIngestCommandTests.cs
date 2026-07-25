using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Tools;

namespace Trackdub.Benchmarks.Tests;

public sealed class MediaIngestCommandOptionsTests
{
    [Fact]
    public void TryParse_CreateModeParsesExpectedValues()
    {
        string projectPath = Path.Combine("artifacts", "Sample.trackdub");
        string mediaPath = Path.Combine("fixtures", "sample.mp4");

        bool success = MediaIngestCommandOptions.TryParse(
            ["--project", projectPath, "--name", "Sample", "--media", mediaPath, "--ffmpeg", "ffmpeg.exe", "--ffprobe", "ffprobe.exe"],
            TextWriter.Null,
            out MediaIngestCommandOptions options);

        Assert.True(success);
        Assert.False(options.ShowHelp);
        Assert.False(options.OpenExistingProject);
        Assert.Equal(Path.GetFullPath(projectPath), options.ProjectRootPath);
        Assert.Equal("Sample", options.ProjectName);
        Assert.Equal(Path.GetFullPath(mediaPath), options.SourceMediaPath);
        Assert.EndsWith("ffmpeg.exe", options.FfmpegPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ffprobe.exe", options.FfprobePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_OpenModeParsesExpectedValues()
    {
        string projectPath = Path.Combine("artifacts", "Sample.trackdub");

        bool success = MediaIngestCommandOptions.TryParse(
            ["--project", projectPath, "--open"],
            TextWriter.Null,
            out MediaIngestCommandOptions options);

        Assert.True(success);
        Assert.True(options.OpenExistingProject);
        Assert.Null(options.ProjectName);
        Assert.Null(options.SourceMediaPath);
    }

    [Fact]
    public void TryParse_RejectsMissingCreateArguments()
    {
        bool success = MediaIngestCommandOptions.TryParse(
            ["--project", ".\\Sample.trackdub", "--name", "Sample"],
            TextWriter.Null,
            out _);

        Assert.False(success);
    }
}

public sealed class MediaIngestCommandTests
{
    [Fact]
    public async Task RunAsync_CreateModePrintsArtifactSummary()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Benchmarks.Tests", Guid.NewGuid().ToString("N"), "Sample.trackdub");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeMediaIngestCommandRunner();

        int exitCode = await MediaIngestCommand.RunAsync(
            ["--project", projectRoot, "--name", "Sample", "--media", "sample.mp4"],
            output,
            error,
            runner,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Created project: Sample", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Normalized audio:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Waveform summary:", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(projectRoot), runner.LastOptions!.ProjectRootPath);
        Assert.Equal("Sample", runner.LastOptions.ProjectName);
    }

    [Fact]
    public async Task RunAsync_OpenModePrintsMissingSourceStatus()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Benchmarks.Tests", Guid.NewGuid().ToString("N"), "Sample.trackdub");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeMediaIngestCommandRunner();

        int exitCode = await MediaIngestCommand.RunAsync(
            ["--project", projectRoot, "--open"],
            output,
            error,
            runner,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("Source status: Missing", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Source message:", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeMediaIngestCommandRunner : IMediaIngestCommandRunner
    {
        public MediaIngestCommandOptions? LastOptions { get; private set; }

        public Task<CreateProjectFromMediaResult> CreateAsync(MediaIngestCommandOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), options.ProjectName!, now, now);
            var mediaAsset = new MediaAsset(Guid.NewGuid(), project.Id, options.SourceMediaPath!, "sample.mp4", "hash-source", 1024, now, "mov,mp4,m4a,3gp,3g2,mj2", 1.25, true, true, now);
            var sourceReference = new SourceMediaReference(
                options.SourceMediaPath!,
                "sample.mp4",
                new FileFingerprint("hash-source", 1024, now),
                new MediaProbeSnapshot("mov,mp4,m4a,3gp,3g2,mj2", "QuickTime / MOV", 1.25, 2048, [new MediaAudioStream(0, "aac", 2, 44100, 1.25)], [new MediaVideoStream(1, "h264", 64, 64, 24, 1.25)]),
                now);
            var audioArtifact = new ProjectArtifact(Guid.NewGuid(), project.Id, mediaAsset.Id, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath, "hash-audio", 2048, 1.25, 48000, 1, now);
            var waveformArtifact = new ProjectArtifact(Guid.NewGuid(), project.Id, mediaAsset.Id, ArtifactKind.WaveformSummary, ProjectArtifactPaths.WaveformSummaryRelativePath, "hash-waveform", 512, 1.25, 48000, 1, now);
            var stemArtifact = new ProjectArtifact(Guid.NewGuid(), project.Id, mediaAsset.Id, ArtifactKind.StemSeparationSourceAudio, ProjectArtifactPaths.StemSeparationSourceAudioRelativePath, "hash-stem", 2048, 1.25, 44100, 1, now);
            return Task.FromResult(new CreateProjectFromMediaResult(project, mediaAsset, sourceReference, audioArtifact, waveformArtifact, stemArtifact));
        }

        public Task<OpenProjectResult> OpenAsync(MediaIngestCommandOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string sourcePath = Path.GetFullPath(Path.Combine("media", "sample.mp4"));
            var project = new TrackdubProject(Guid.NewGuid(), "Sample", now, now);
            var mediaAsset = new MediaAsset(Guid.NewGuid(), project.Id, sourcePath, "sample.mp4", "hash-source", 1024, now, "mov,mp4,m4a,3gp,3g2,mj2", 1.25, true, true, now);
            var sourceReference = new SourceMediaReference(
                sourcePath,
                "sample.mp4",
                new FileFingerprint("hash-source", 1024, now),
                new MediaProbeSnapshot("mov,mp4,m4a,3gp,3g2,mj2", "QuickTime / MOV", 1.25, 2048, [new MediaAudioStream(0, "aac", 2, 44100, 1.25)], [new MediaVideoStream(1, "h264", 64, 64, 24, 1.25)]),
                now);
            IReadOnlyList<ProjectArtifact> artifacts =
            [
                new ProjectArtifact(Guid.NewGuid(), project.Id, mediaAsset.Id, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath, "hash-audio", 2048, 1.25, 48000, 1, now)
            ];
            return Task.FromResult(new OpenProjectResult(project, mediaAsset, sourceReference, SourceMediaStatus.Missing, $"Source media file was not found at '{sourcePath}'.", artifacts, TranscriptLanguage: null));
        }
    }
}
