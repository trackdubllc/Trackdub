using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class ProjectMediaIngestServiceTests
{
    [Fact]
    public async Task CreateAsync_registers_media_and_artifacts()
    {
        var fileSystemProbe = new FakeFileSystemProbe();
        string sourcePath = Path.Combine("virtual-media", "sample.mp4");
        fileSystemProbe.SeedExistingFile(sourcePath);

        var projectRepository = new FakeProjectRepository();
        var mediaRepository = new FakeMediaAssetRepository();
        var artifactStore = new FakeArtifactStore();
        var service = new ProjectMediaIngestService(
            projectRepository,
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            new FakeAudioExtractionService(),
            new FakeWaveformSummaryGenerator(),
            new FakeFileFingerprintService(),
            fileSystemProbe);

        CreateProjectFromMediaResult result = await service.CreateAsync(
            new CreateProjectFromMediaRequest("Sample Project", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal("Sample Project", result.Project.Name);
        Assert.Equal("sample.mp4", result.SourceReference.OriginalFileName);
        Assert.Contains(ProjectArtifactPaths.ManifestRelativePath, artifactStore.JsonWrites.Keys);
        Assert.Contains(ProjectArtifactPaths.SourceReferenceRelativePath, artifactStore.JsonWrites.Keys);
        Assert.Collection(
            mediaRepository.Artifacts,
            a => Assert.Equal(ArtifactKind.NormalizedAudio, a.Kind),
            a => Assert.Equal(ArtifactKind.StemSeparationSourceAudio, a.Kind),
            a => Assert.Equal(ArtifactKind.WaveformSummary, a.Kind));
    }

    [Fact]
    public async Task CreateAsync_accepts_seeded_source_path_without_real_file()
    {
        var fileSystemProbe = new FakeFileSystemProbe();
        string sourcePath = Path.Combine("virtual-media", "sample.mp4");
        fileSystemProbe.SeedExistingFile(sourcePath);

        var service = new ProjectMediaIngestService(
            new FakeProjectRepository(),
            new FakeMediaAssetRepository(),
            new FakeArtifactStore(),
            new FakeMediaProbe(),
            new FakeAudioExtractionService(),
            new FakeWaveformSummaryGenerator(),
            new FakeFileFingerprintService(),
            fileSystemProbe);

        CreateProjectFromMediaResult result = await service.CreateAsync(
            new CreateProjectFromMediaRequest("Sample Project", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal(fileSystemProbe.GetFullPath(sourcePath), result.SourceReference.OriginalPath);
    }

    [Fact]
    public async Task CreateMediaSpineAsync_registers_source_media_without_heavy_ingest()
    {
        var fileSystemProbe = new FakeFileSystemProbe();
        string sourcePath = Path.Combine("virtual-media", "sample.mp4");
        string fullSourcePath = fileSystemProbe.GetFullPath(sourcePath);
        fileSystemProbe.SeedExistingFile(sourcePath);

        var projectRepository = new FakeProjectRepository();
        var mediaRepository = new FakeMediaAssetRepository();
        var artifactStore = new FakeArtifactStore();
        var audioExtractionService = new FakeAudioExtractionService();
        var waveformSummaryGenerator = new FakeWaveformSummaryGenerator();
        var service = new ProjectMediaIngestService(
            projectRepository,
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            audioExtractionService,
            waveformSummaryGenerator,
            new FakeFileFingerprintService(),
            fileSystemProbe);

        OpenProjectResult result = await service.CreateMediaSpineAsync(
            new CreateProjectFromMediaRequest("Sample Project", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal("Sample Project", result.Project.Name);
        Assert.NotNull(result.MediaAsset);
        Assert.Equal(fullSourcePath, result.MediaAsset!.SourceFilePath);
        Assert.Equal("sample.mp4", result.MediaAsset.SourceFileName);
        Assert.Equal(SourceMediaStatus.Available, result.SourceStatus);
        Assert.Empty(result.Artifacts);
        Assert.Equal(0, audioExtractionService.ExtractCallCount);
        Assert.Equal(0, waveformSummaryGenerator.GenerateCallCount);
        Assert.Contains(ProjectArtifactPaths.ManifestRelativePath, artifactStore.JsonWrites.Keys);
        Assert.Contains(ProjectArtifactPaths.SourceReferenceRelativePath, artifactStore.JsonWrites.Keys);
    }

    [Fact]
    public async Task OpenAsync_reopens_media_spine_project_with_source_reference_intact()
    {
        var fileSystemProbe = new FakeFileSystemProbe();
        string sourcePath = Path.Combine("virtual-media", "sample.mp4");
        string fullSourcePath = fileSystemProbe.GetFullPath(sourcePath);
        fileSystemProbe.SeedExistingFile(sourcePath);

        var projectRepository = new FakeProjectRepository();
        var mediaRepository = new FakeMediaAssetRepository();
        var artifactStore = new FakeArtifactStore();
        var service = new ProjectMediaIngestService(
            projectRepository,
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            new FakeAudioExtractionService(),
            new FakeWaveformSummaryGenerator(),
            new FakeFileFingerprintService(),
            fileSystemProbe);

        await service.CreateMediaSpineAsync(
            new CreateProjectFromMediaRequest("Sample Project", sourcePath),
            TestContext.Current.CancellationToken);

        OpenProjectResult reopened = await service.OpenAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reopened.SourceReference);
        Assert.Equal(fullSourcePath, reopened.SourceReference!.OriginalPath);
        Assert.NotNull(reopened.MediaAsset);
        Assert.Equal(fullSourcePath, reopened.MediaAsset!.SourceFilePath);
        Assert.Equal(SourceMediaStatus.Available, reopened.SourceStatus);
        Assert.Empty(reopened.Artifacts);
    }

    [Fact]
    public async Task OpenAsync_reports_missing_source_file_clearly()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Missing Source", now, now);
        var mediaAsset = new MediaAsset(Guid.NewGuid(), project.Id, @"C:\media\missing.mp4", "missing.mp4", "hash", 100, now, "mp4", 1.0, true, true, now);
        var projectRepository = new FakeProjectRepository(project);
        var mediaRepository = new FakeMediaAssetRepository(mediaAsset);
        var artifactStore = new FakeArtifactStore();
        var fileSystemProbe = new FakeFileSystemProbe();
        artifactStore.Seed(ProjectArtifactPaths.SourceReferenceRelativePath, new SourceMediaReference(
            @"C:\media\missing.mp4",
            "missing.mp4",
            new FileFingerprint("hash", 100, now),
            new MediaProbeSnapshot("mp4", "mp4", 1.0, null, [new MediaAudioStream(0, "aac", 2, 44100, 1.0)], []),
            now));
        mediaRepository.Artifacts.Add(new ProjectArtifact(Guid.NewGuid(), project.Id, mediaAsset.Id, ArtifactKind.NormalizedAudio, "media/normalized_audio.wav", "artifact-hash", 100, 1.0, 48000, 1, now));

        var service = new ProjectMediaIngestService(
            projectRepository,
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            new FakeAudioExtractionService(),
            new FakeWaveformSummaryGenerator(),
            new FakeFileFingerprintService(),
            fileSystemProbe);

        OpenProjectResult result = await service.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceMediaStatus.Missing, result.SourceStatus);
        Assert.Contains("Source media file was not found", result.SourceStatusMessage);
        Assert.Single(result.Artifacts);
    }

    [Fact]
    public async Task RelocateSourceAsync_updates_source_reference_and_media_asset_path()
    {
        var fileSystemProbe = new FakeFileSystemProbe();
        string relocatedSourcePath = Path.Combine("virtual-media", "relocated.mp4");
        string fullRelocatedSourcePath = fileSystemProbe.GetFullPath(relocatedSourcePath);
        fileSystemProbe.SeedExistingFile(relocatedSourcePath);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Relocate Source", now, now);
        var mediaAsset = new MediaAsset(Guid.NewGuid(), project.Id, @"C:\media\missing.mp4", "missing.mp4", "computed-hash", 4, now, "mp4", 1.0, true, true, now);
        var projectRepository = new FakeProjectRepository(project);
        var mediaRepository = new FakeMediaAssetRepository(mediaAsset);
        var artifactStore = new FakeArtifactStore();
        var fileFingerprintService = new FakeFileFingerprintService(new FileFingerprint("computed-hash", 4, now));
        artifactStore.Seed(ProjectArtifactPaths.SourceReferenceRelativePath, new SourceMediaReference(
            @"C:\media\missing.mp4",
            "missing.mp4",
            new FileFingerprint("computed-hash", 4, now),
            new MediaProbeSnapshot("mp4", "mp4", 1.0, null, [new MediaAudioStream(0, "aac", 2, 44100, 1.0)], []),
            now));

        var service = new ProjectMediaIngestService(
            projectRepository,
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            new FakeAudioExtractionService(),
            new FakeWaveformSummaryGenerator(),
            fileFingerprintService,
            fileSystemProbe);

        OpenProjectResult result = await service.RelocateSourceAsync(
            new RelocateSourceMediaRequest(relocatedSourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal(SourceMediaStatus.Available, result.SourceStatus);
        Assert.NotNull(result.SourceReference);
        Assert.Equal(fullRelocatedSourcePath, result.SourceReference!.OriginalPath);
        Assert.Equal(fullRelocatedSourcePath, mediaRepository.MediaAsset!.SourceFilePath);
        Assert.Equal("relocated.mp4", mediaRepository.MediaAsset.SourceFileName);
    }

    [Fact]
    public async Task RenameProjectAsync_rolls_back_manifest_when_project_update_fails()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Original Name", now, now);
        var projectRepository = new FakeProjectRepository(project)
        {
            ThrowOnUpdate = true
        };
        var artifactStore = new FakeArtifactStore();
        var originalManifest = ProjectManifest.FromProject(project, "en");
        artifactStore.Seed(ProjectArtifactPaths.ManifestRelativePath, originalManifest);
        var service = new ProjectMediaIngestService(
            projectRepository,
            new FakeMediaAssetRepository(),
            artifactStore,
            new FakeMediaProbe(),
            new FakeAudioExtractionService(),
            new FakeWaveformSummaryGenerator(),
            new FakeFileFingerprintService(),
            new FakeFileSystemProbe());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RenameProjectAsync(new RenameProjectRequest("New Name"), TestContext.Current.CancellationToken));
        ProjectManifest? manifest = await artifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            TestContext.Current.CancellationToken);

        Assert.NotNull(manifest);
        Assert.Equal("Original Name", manifest!.Name);
        Assert.Equal("en", manifest.TranscriptLanguage);
    }

    [Fact]
    public async Task EnsureNormalizedAudioAsync_extracts_when_media_spine_has_no_normalized_artifact()
    {
        var fileSystemProbe = new FakeFileSystemProbe();
        string sourcePath = Path.Combine("virtual-media", "sample.mp4");
        string fullSourcePath = fileSystemProbe.GetFullPath(sourcePath);
        fileSystemProbe.SeedExistingFile(sourcePath);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Sample", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            project.Id,
            fullSourcePath,
            "sample.mp4",
            "hash-sample.mp4",
            4,
            now,
            "mp4",
            1.25,
            HasAudio: true,
            HasVideo: true,
            now);
        var mediaRepository = new FakeMediaAssetRepository(mediaAsset);
        var artifactStore = new FakeArtifactStore();
        var audioExtractionService = new FakeAudioExtractionService();
        var waveformSummaryGenerator = new FakeWaveformSummaryGenerator();
        var service = new ProjectMediaIngestService(
            new FakeProjectRepository(project),
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            audioExtractionService,
            waveformSummaryGenerator,
            new FakeFileFingerprintService(),
            fileSystemProbe);

        ProjectArtifact normalized = await service.EnsureNormalizedAudioAsync(
            mediaAsset,
            [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ArtifactKind.NormalizedAudio, normalized.Kind);
        Assert.Equal("normalized-audio-refresh:media-spine", normalized.Provenance);
        Assert.Equal(1, audioExtractionService.ExtractCallCount);
        Assert.Equal(1, waveformSummaryGenerator.GenerateCallCount);
        Assert.Contains(mediaRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.NormalizedAudio);
        Assert.Contains(mediaRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.WaveformSummary);
        Assert.Contains(ProjectArtifactPaths.WaveformSummaryRelativePath, artifactStore.JsonWrites.Keys);
    }

    [Fact]
    public async Task EnsureNormalizedAudioAsync_returns_existing_without_re_extracting()
    {
        var fileSystemProbe = new FakeFileSystemProbe();
        string sourcePath = Path.Combine("virtual-media", "sample.mp4");
        fileSystemProbe.SeedExistingFile(sourcePath);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Sample", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            project.Id,
            fileSystemProbe.GetFullPath(sourcePath),
            "sample.mp4",
            "hash-sample.mp4",
            4,
            now,
            "mp4",
            1.25,
            HasAudio: true,
            HasVideo: true,
            now);
        var existing = new ProjectArtifact(
            Guid.NewGuid(),
            project.Id,
            mediaAsset.Id,
            ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            "existing-hash",
            4,
            1.25,
            48000,
            1,
            now);
        var audioExtractionService = new FakeAudioExtractionService();
        var service = new ProjectMediaIngestService(
            new FakeProjectRepository(project),
            new FakeMediaAssetRepository(mediaAsset),
            new FakeArtifactStore(),
            new FakeMediaProbe(),
            audioExtractionService,
            new FakeWaveformSummaryGenerator(),
            new FakeFileFingerprintService(),
            fileSystemProbe);

        ProjectArtifact result = await service.EnsureNormalizedAudioAsync(
            mediaAsset,
            [existing],
            TestContext.Current.CancellationToken);

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal(0, audioExtractionService.ExtractCallCount);
    }

    [Fact]
    public async Task EnsureStereoNormalizedAudioAsync_refreshes_mono_artifact_when_source_matches()
    {
        var fileSystemProbe = new FakeFileSystemProbe();
        string sourcePath = Path.Combine("virtual-media", "sample.mp4");
        string fullSourcePath = fileSystemProbe.GetFullPath(sourcePath);
        fileSystemProbe.SeedExistingFile(sourcePath);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Sample", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            project.Id,
            fullSourcePath,
            "sample.mp4",
            "hash-sample.mp4",
            4,
            now,
            "mp4",
            1.25,
            HasAudio: true,
            HasVideo: true,
            now);
        var normalized = new ProjectArtifact(
            Guid.NewGuid(),
            project.Id,
            mediaAsset.Id,
            ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            "old-hash",
            4,
            1.25,
            48000,
            1,
            now);
        var mediaRepository = new FakeMediaAssetRepository(mediaAsset);
        mediaRepository.Artifacts.Add(normalized);
        var artifactStore = new FakeArtifactStore();
        var service = new ProjectMediaIngestService(
            new FakeProjectRepository(project),
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            new FakeAudioExtractionService(),
            new FakeWaveformSummaryGenerator(),
            new FakeFileFingerprintService(),
            fileSystemProbe);

        ProjectArtifact refreshed = await service.EnsureStereoNormalizedAudioAsync(
            mediaAsset,
            normalized,
            TestContext.Current.CancellationToken);

        Assert.Equal(normalized.Id, refreshed.Id);
        Assert.Equal(2, refreshed.ChannelCount);
        Assert.Equal("normalized-audio-refresh:stereo-stem-source", refreshed.Provenance);
        Assert.Contains(ProjectArtifactPaths.WaveformSummaryRelativePath, artifactStore.JsonWrites.Keys);
        Assert.Contains(mediaRepository.Artifacts, artifact => artifact.Id == normalized.Id && artifact.ChannelCount == 2);
    }

    private sealed class FakeProjectRepository(TrackdubProject? project = null) : IProjectRepository
    {
        private TrackdubProject? project = project;

        public bool ThrowOnUpdate { get; init; }

        public Task InitializeAsync(TrackdubProject project, CancellationToken cancellationToken)
        {
            this.project = project;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(TrackdubProject project, CancellationToken cancellationToken)
        {
            if (ThrowOnUpdate)
            {
                throw new InvalidOperationException("Project update failed.");
            }

            this.project = project;
            return Task.CompletedTask;
        }

        public Task<TrackdubProject?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(project);
    }

    private sealed class FakeMediaAssetRepository(MediaAsset? mediaAsset = null) : IMediaAssetRepository
    {
        public MediaAsset? MediaAsset { get; private set; } = mediaAsset;

        public List<ProjectArtifact> Artifacts { get; } = [];

        public Task SaveAsync(MediaAsset asset, CancellationToken cancellationToken)
        {
            MediaAsset = asset;
            return Task.CompletedTask;
        }

        public Task UpdateSourcePathAsync(
            Guid mediaAssetId,
            string sourceFilePath,
            string sourceFileName,
            CancellationToken cancellationToken)
        {
            if (MediaAsset is not null && MediaAsset.Id == mediaAssetId)
            {
                MediaAsset = MediaAsset with
                {
                    SourceFilePath = sourceFilePath,
                    SourceFileName = sourceFileName
                };
            }

            return Task.CompletedTask;
        }

        public Task<MediaAsset?> GetPrimaryAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult(MediaAsset);

        public Task SaveArtifactAsync(ProjectArtifact artifact, CancellationToken cancellationToken)
        {
            Artifacts.Add(artifact);
            return Task.CompletedTask;
        }

        public Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            Artifacts.RemoveAll(artifact => artifact.Id == artifactId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectArtifact>> GetArtifactsAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectArtifact>>(Artifacts);

        public Task<IReadOnlyList<MediaAsset>> GetAllAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaAsset>>(MediaAsset is not null && MediaAsset.ProjectId == projectId ? new List<MediaAsset> { MediaAsset } : new List<MediaAsset>());

        public Task<ProjectArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            ProjectArtifact? artifact = Artifacts.FirstOrDefault(a => a.Id == artifactId);
            return Task.FromResult(artifact);
        }
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        private readonly Dictionary<string, object> reads = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object> JsonWrites { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task EnsureLayoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ArtifactWriteHandle CreateWriteHandle(string relativePath) =>
            new(relativePath, Path.Combine("project", relativePath), Path.Combine("project", "temp", Path.GetFileName(relativePath)));

        public Task CommitAsync(ArtifactWriteHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
        {
            JsonWrites[relativePath] = value!;
            reads[relativePath] = value!;
            return Task.CompletedTask;
        }

        public Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
        {
            if (reads.TryGetValue(relativePath, out object? value))
            {
                return Task.FromResult((T?)value);
            }

            return Task.FromResult<T?>(default);
        }

        public string GetPath(string relativePath) => Path.Combine("project", relativePath);

        public bool Exists(string relativePath) => reads.ContainsKey(relativePath);

        public void Seed<T>(string relativePath, T value) where T : notnull
        {
            reads[relativePath] = value;
        }
    }

    private sealed class FakeMediaProbe : IMediaProbe
    {
        public Task<MediaProbeSnapshot> ProbeAsync(string sourcePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeSnapshot(
                "mov,mp4,m4a,3gp,3g2,mj2",
                "QuickTime / MOV",
                1.25,
                1024,
                [new MediaAudioStream(0, "aac", 2, 44100, 1.25)],
                [new MediaVideoStream(1, "h264", 64, 64, 24, 1.25)]));
    }

    private sealed class FakeAudioExtractionService : IAudioExtractionService
    {
        public int ExtractCallCount { get; private set; }

        public async Task<AudioExtractionResult> ExtractNormalizedAudioAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken,
            int? maxEncoderThreads = null)
        {
            ExtractCallCount++;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, [1, 2, 3, 4], cancellationToken);
            return new AudioExtractionResult(destinationPath, 1.25, 48000, 2, 60000);
        }

        public async Task<AudioExtractionResult> ExtractStemSeparationAudioAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            ExtractCallCount++;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, [1, 2, 3, 4], cancellationToken);
            return new AudioExtractionResult(destinationPath, 1.25, 44100, 2, 55125);
        }
    }

    private sealed class FakeWaveformSummaryGenerator : IWaveformSummaryGenerator
    {
        public int GenerateCallCount { get; private set; }

        public Task<WaveformSummary> GenerateAsync(string audioPath, CancellationToken cancellationToken)
        {
            GenerateCallCount++;
            return Task.FromResult(new WaveformSummary(4, 48000, 2, 1.25, [0.1f, 0.4f, 0.8f, 0.2f]));
        }
    }
}
