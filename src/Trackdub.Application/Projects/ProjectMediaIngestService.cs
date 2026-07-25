using System.Linq;
using Trackdub.Contracts;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;

namespace Trackdub.Application.Projects;

public sealed class ProjectMediaIngestService(
    IProjectRepository projectRepository,
    IMediaAssetRepository mediaAssetRepository,
    IArtifactStore artifactStore,
    IMediaProbe mediaProbe,
    IAudioExtractionService audioExtractionService,
    IWaveformSummaryGenerator waveformSummaryGenerator,
    IFileFingerprintService fileFingerprintService,
    IFileSystemProbe fileSystemProbe,
    IApplicationLogger? applicationLogger = null)
{
    private readonly IApplicationLogger? logger = applicationLogger;

    private static MediaAsset? GetPrimaryAsset(IReadOnlyList<MediaAsset> assets) =>
        assets.FirstOrDefault();

    public async Task<CreateProjectFromMediaResult> CreateAsync(
        CreateProjectFromMediaRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceMediaPath);

        string fullSourcePath = fileSystemProbe.GetFullPath(request.SourceMediaPath);
        if (!fileSystemProbe.FileExists(fullSourcePath))
        {
            throw new FileNotFoundException("Source media file was not found.", fullSourcePath);
        }

        await artifactStore.EnsureLayoutAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), request.ProjectName.Trim(), now, now);
        await projectRepository.InitializeAsync(project, cancellationToken).ConfigureAwait(false);

        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.ManifestRelativePath,
            ProjectManifest.FromProject(project),
            cancellationToken).ConfigureAwait(false);

        MediaProbeSnapshot probe = await mediaProbe.ProbeAsync(fullSourcePath, cancellationToken).ConfigureAwait(false);
        FileFingerprint sourceFingerprint = await fileFingerprintService.ComputeAsync(fullSourcePath, cancellationToken).ConfigureAwait(false);

        var sourceReference = new SourceMediaReference(
            fullSourcePath,
            Path.GetFileName(fullSourcePath),
            sourceFingerprint,
            probe,
            now);

        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.SourceReferenceRelativePath,
            sourceReference,
            cancellationToken).ConfigureAwait(false);

        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            project.Id,
            fullSourcePath,
            sourceReference.OriginalFileName,
            sourceFingerprint.Sha256,
            sourceFingerprint.SizeBytes,
            sourceFingerprint.LastWriteTimeUtc,
            probe.FormatName,
            probe.DurationSeconds,
            probe.AudioStreams.Count > 0,
            probe.VideoStreams.Count > 0,
            now);

        await mediaAssetRepository.SaveAsync(mediaAsset, cancellationToken).ConfigureAwait(false);

        ArtifactWriteHandle audioWriteHandle = artifactStore.CreateWriteHandle(ProjectArtifactPaths.NormalizedAudioRelativePath);
        ArtifactWriteHandle stemWriteHandle = artifactStore.CreateWriteHandle(ProjectArtifactPaths.StemSeparationSourceAudioRelativePath);
        try
        {
            AudioExtractionResult extraction = await audioExtractionService.ExtractNormalizedAudioAsync(
                fullSourcePath,
                audioWriteHandle.TemporaryPath,
                cancellationToken).ConfigureAwait(false);
            await artifactStore.CommitAsync(audioWriteHandle, cancellationToken).ConfigureAwait(false);

            AudioExtractionResult stemExtraction = await audioExtractionService.ExtractStemSeparationAudioAsync(
                fullSourcePath,
                stemWriteHandle.TemporaryPath,
                cancellationToken).ConfigureAwait(false);
            await artifactStore.CommitAsync(stemWriteHandle, cancellationToken).ConfigureAwait(false);

            FileFingerprint audioFingerprint = await fileFingerprintService.ComputeAsync(
                audioWriteHandle.FinalPath,
                cancellationToken).ConfigureAwait(false);

            FileFingerprint stemFingerprint = await fileFingerprintService.ComputeAsync(
                stemWriteHandle.FinalPath,
                cancellationToken).ConfigureAwait(false);

            var audioArtifact = new ProjectArtifact(
                Guid.NewGuid(),
                project.Id,
                mediaAsset.Id,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                audioFingerprint.Sha256,
                audioFingerprint.SizeBytes,
                extraction.DurationSeconds,
                extraction.SampleRate,
                extraction.ChannelCount,
                now);

            var stemArtifact = new ProjectArtifact(
                Guid.NewGuid(),
                project.Id,
                mediaAsset.Id,
                ArtifactKind.StemSeparationSourceAudio,
                ProjectArtifactPaths.StemSeparationSourceAudioRelativePath,
                stemFingerprint.Sha256,
                stemFingerprint.SizeBytes,
                stemExtraction.DurationSeconds,
                stemExtraction.SampleRate,
                stemExtraction.ChannelCount,
                now);

            await mediaAssetRepository.SaveArtifactAsync(audioArtifact, cancellationToken).ConfigureAwait(false);
            await mediaAssetRepository.SaveArtifactAsync(stemArtifact, cancellationToken).ConfigureAwait(false);

            WaveformSummary waveform = await waveformSummaryGenerator.GenerateAsync(
                audioWriteHandle.FinalPath,
                cancellationToken).ConfigureAwait(false);

            await artifactStore.WriteJsonAsync(
                ProjectArtifactPaths.WaveformSummaryRelativePath,
                waveform,
                cancellationToken).ConfigureAwait(false);

            FileFingerprint waveformFingerprint = await fileFingerprintService.ComputeAsync(
                artifactStore.GetPath(ProjectArtifactPaths.WaveformSummaryRelativePath),
                cancellationToken).ConfigureAwait(false);

            var waveformArtifact = new ProjectArtifact(
                Guid.NewGuid(),
                project.Id,
                mediaAsset.Id,
                ArtifactKind.WaveformSummary,
                ProjectArtifactPaths.WaveformSummaryRelativePath,
                waveformFingerprint.Sha256,
                waveformFingerprint.SizeBytes,
                waveform.DurationSeconds,
                waveform.SampleRate,
                waveform.ChannelCount,
                now);

            await mediaAssetRepository.SaveArtifactAsync(waveformArtifact, cancellationToken).ConfigureAwait(false);

            return new CreateProjectFromMediaResult(
                project,
                mediaAsset,
                sourceReference,
                audioArtifact,
                waveformArtifact,
                stemArtifact);
        }
        catch
        {
            // Ensure temporary file is cleaned up if operation fails
            await audioWriteHandle.DisposeAsync().ConfigureAwait(false);
            await stemWriteHandle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<OpenProjectResult> CreateMediaSpineAsync(
        CreateProjectFromMediaRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceMediaPath);

        string fullSourcePath = fileSystemProbe.GetFullPath(request.SourceMediaPath);
        if (!fileSystemProbe.FileExists(fullSourcePath))
        {
            throw new FileNotFoundException("Source media file was not found.", fullSourcePath);
        }

        await artifactStore.EnsureLayoutAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), request.ProjectName.Trim(), now, now);
        await projectRepository.InitializeAsync(project, cancellationToken).ConfigureAwait(false);

        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.ManifestRelativePath,
            ProjectManifest.FromProject(project),
            cancellationToken).ConfigureAwait(false);

        MediaProbeSnapshot probe = await mediaProbe.ProbeAsync(fullSourcePath, cancellationToken).ConfigureAwait(false);
        FileFingerprint sourceFingerprint = await fileFingerprintService.ComputeAsync(fullSourcePath, cancellationToken).ConfigureAwait(false);

        var sourceReference = new SourceMediaReference(
            fullSourcePath,
            Path.GetFileName(fullSourcePath),
            sourceFingerprint,
            probe,
            now);

        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.SourceReferenceRelativePath,
            sourceReference,
            cancellationToken).ConfigureAwait(false);

        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            project.Id,
            fullSourcePath,
            sourceReference.OriginalFileName,
            sourceFingerprint.Sha256,
            sourceFingerprint.SizeBytes,
            sourceFingerprint.LastWriteTimeUtc,
            probe.FormatName,
            probe.DurationSeconds,
            probe.AudioStreams.Count > 0,
            probe.VideoStreams.Count > 0,
            now);

        await mediaAssetRepository.SaveAsync(mediaAsset, cancellationToken).ConfigureAwait(false);

        OpenProjectResult result = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MediaVideoStream? video = probe.VideoStreams.FirstOrDefault();
        bool normalizedAudioPresent = result.Artifacts.Any(static artifact => artifact.Kind == ArtifactKind.NormalizedAudio);
        logger?.LogInformation(
            $"Project import bare media spine created: projectName={request.ProjectName}, " +
            $"source={fullSourcePath}, probeFormat={probe.FormatName}, " +
            $"probeDuration={probe.DurationSeconds:F3}, probeSize={video?.Width ?? 0}x{video?.Height ?? 0}, " +
            $"probeAudioStreams={probe.AudioStreams.Count}, probeVideoStreams={probe.VideoStreams.Count}, " +
            $"sourceHasAudio={probe.AudioStreams.Count > 0}, sourceHasVideo={probe.VideoStreams.Count > 0}, " +
            $"artifacts={result.Artifacts.Count}, normalizedAudioPresent={normalizedAudioPresent}.");
        return result;
    }

    public async Task<OpenProjectResult> OpenAsync(CancellationToken cancellationToken)
    {
        TrackdubProject? project = await projectRepository.GetAsync(cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            throw new InvalidOperationException("Project database does not contain a project record.");
        }

        ProjectManifest? manifest = await artifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            cancellationToken).ConfigureAwait(false);
        SourceMediaReference? sourceReference = await artifactStore.ReadJsonAsync<SourceMediaReference>(
            ProjectArtifactPaths.SourceReferenceRelativePath,
            cancellationToken).ConfigureAwait(false);

        MediaAsset? mediaAsset = GetPrimaryAsset(await mediaAssetRepository.GetAllAsync(project.Id, cancellationToken).ConfigureAwait(false));
        IReadOnlyList<ProjectArtifact> artifacts = await mediaAssetRepository.GetArtifactsAsync(project.Id, cancellationToken).ConfigureAwait(false);
        (SourceMediaStatus status, string? message) = await ResolveSourceStatusAsync(sourceReference, cancellationToken).ConfigureAwait(false);

        return new OpenProjectResult(
            project,
            mediaAsset,
            sourceReference,
            status,
            message,
            artifacts,
            manifest?.TranscriptLanguage,
            manifest?.UiSettings?.Normalize());
    }

    public Task<IReadOnlyList<ProjectArtifact>> GetProjectArtifactsAsync(
        Guid projectId,
        CancellationToken cancellationToken) =>
        mediaAssetRepository.GetArtifactsAsync(projectId, cancellationToken);

    public async Task SaveUiSettingsAsync(
        ProjectUiSettings? uiSettings,
        CancellationToken cancellationToken)
    {
        TrackdubProject? project = await projectRepository.GetAsync(cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            throw new InvalidOperationException("Project database does not contain a project record.");
        }

        ProjectManifest? existingManifest = await artifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            cancellationToken).ConfigureAwait(false);
        ProjectManifest manifest = existingManifest ?? ProjectManifest.FromProject(project);

        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.ManifestRelativePath,
            manifest.WithUiSettings(uiSettings),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OpenProjectResult> RelocateSourceAsync(
        RelocateSourceMediaRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NewSourceMediaPath);

        TrackdubProject? project = await projectRepository.GetAsync(cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            throw new InvalidOperationException("Project database does not contain a project record.");
        }

        SourceMediaReference? existingSourceReference = await artifactStore.ReadJsonAsync<SourceMediaReference>(
            ProjectArtifactPaths.SourceReferenceRelativePath,
            cancellationToken).ConfigureAwait(false);
        MediaAsset mediaAsset = (await mediaAssetRepository.GetAllAsync(project.Id, cancellationToken).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new InvalidOperationException("The project does not contain a primary media asset.");

        string fullSourcePath = fileSystemProbe.GetFullPath(request.NewSourceMediaPath);
        if (!fileSystemProbe.FileExists(fullSourcePath))
        {
            throw new FileNotFoundException("Relocated source media file was not found.", fullSourcePath);
        }

        FileFingerprint fingerprint = await fileFingerprintService.ComputeAsync(fullSourcePath, cancellationToken).ConfigureAwait(false);
        string expectedFingerprint = existingSourceReference?.Fingerprint.Sha256 ?? mediaAsset.FingerprintSha256;

        if (!string.Equals(fingerprint.Sha256, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The selected file does not match the ingested source media fingerprint. Expected '{expectedFingerprint}', but found '{fingerprint.Sha256}'.");
        }

        MediaProbeSnapshot probe = await mediaProbe.ProbeAsync(fullSourcePath, cancellationToken).ConfigureAwait(false);
        var updatedReference = new SourceMediaReference(
            fullSourcePath,
            Path.GetFileName(fullSourcePath),
            fingerprint,
            probe,
            DateTimeOffset.UtcNow);

        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.SourceReferenceRelativePath,
            updatedReference,
            cancellationToken).ConfigureAwait(false);
        await mediaAssetRepository.UpdateSourcePathAsync(
            mediaAsset.Id,
            updatedReference.OriginalPath,
            updatedReference.OriginalFileName,
            cancellationToken).ConfigureAwait(false);

        return await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectArtifact> EnsureStereoNormalizedAudioAsync(
        MediaAsset mediaAsset,
        ProjectArtifact normalizedAudioArtifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaAsset);
        ArgumentNullException.ThrowIfNull(normalizedAudioArtifact);

        if (normalizedAudioArtifact.ChannelCount is >= 2)
        {
            return normalizedAudioArtifact;
        }

        string fullSourcePath = fileSystemProbe.GetFullPath(mediaAsset.SourceFilePath);
        if (!fileSystemProbe.FileExists(fullSourcePath))
        {
            return normalizedAudioArtifact;
        }

        FileFingerprint sourceFingerprint = await fileFingerprintService
            .ComputeAsync(fullSourcePath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(sourceFingerprint.Sha256, mediaAsset.FingerprintSha256, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedAudioArtifact;
        }

        ArtifactWriteHandle audioWriteHandle = artifactStore.CreateWriteHandle(normalizedAudioArtifact.RelativePath);
        try
        {
            AudioExtractionResult extraction = await audioExtractionService.ExtractNormalizedAudioAsync(
                fullSourcePath,
                audioWriteHandle.TemporaryPath,
                cancellationToken).ConfigureAwait(false);
            await artifactStore.CommitAsync(audioWriteHandle, cancellationToken).ConfigureAwait(false);

            FileFingerprint audioFingerprint = await fileFingerprintService
                .ComputeAsync(audioWriteHandle.FinalPath, cancellationToken)
                .ConfigureAwait(false);
            var refreshedAudioArtifact = normalizedAudioArtifact with
            {
                Sha256 = audioFingerprint.Sha256,
                SizeBytes = audioFingerprint.SizeBytes,
                DurationSeconds = extraction.DurationSeconds,
                SampleRate = extraction.SampleRate,
                ChannelCount = extraction.ChannelCount,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Provenance = "normalized-audio-refresh:stereo-stem-source"
            };
            await mediaAssetRepository.SaveArtifactAsync(refreshedAudioArtifact, cancellationToken).ConfigureAwait(false);

            WaveformSummary waveform = await waveformSummaryGenerator
                .GenerateAsync(audioWriteHandle.FinalPath, cancellationToken)
                .ConfigureAwait(false);
            await artifactStore.WriteJsonAsync(
                ProjectArtifactPaths.WaveformSummaryRelativePath,
                waveform,
                cancellationToken).ConfigureAwait(false);
            FileFingerprint waveformFingerprint = await fileFingerprintService
                .ComputeAsync(artifactStore.GetPath(ProjectArtifactPaths.WaveformSummaryRelativePath), cancellationToken)
                .ConfigureAwait(false);

            ProjectArtifact? existingWaveformArtifact = (await mediaAssetRepository
                    .GetArtifactsAsync(mediaAsset.ProjectId, cancellationToken)
                    .ConfigureAwait(false))
                .Where(static artifact => artifact.Kind == ArtifactKind.WaveformSummary)
                .OrderByDescending(static artifact => artifact.CreatedAtUtc)
                .FirstOrDefault();
            var refreshedWaveformArtifact = new ProjectArtifact(
                existingWaveformArtifact?.Id ?? Guid.NewGuid(),
                mediaAsset.ProjectId,
                mediaAsset.Id,
                ArtifactKind.WaveformSummary,
                ProjectArtifactPaths.WaveformSummaryRelativePath,
                waveformFingerprint.Sha256,
                waveformFingerprint.SizeBytes,
                waveform.DurationSeconds,
                waveform.SampleRate,
                waveform.ChannelCount,
                DateTimeOffset.UtcNow,
                Provenance: "waveform-refresh:stereo-stem-source");
            await mediaAssetRepository.SaveArtifactAsync(refreshedWaveformArtifact, cancellationToken).ConfigureAwait(false);

            return refreshedAudioArtifact;
        }
        catch
        {
            await audioWriteHandle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ProjectArtifact> EnsureNormalizedAudioAsync(
        MediaAsset mediaAsset,
        IReadOnlyList<ProjectArtifact> existingArtifacts,
        CancellationToken cancellationToken,
        int? ffmpegThreadBudget = null)
    {
        ArgumentNullException.ThrowIfNull(mediaAsset);
        ArgumentNullException.ThrowIfNull(existingArtifacts);

        ProjectArtifact? existingNormalized = existingArtifacts
            .Where(static artifact => artifact.Kind == ArtifactKind.NormalizedAudio)
            .OrderByDescending(static artifact => artifact.CreatedAtUtc)
            .FirstOrDefault();

        if (existingNormalized is not null)
        {
            string normalizedPath = artifactStore.GetPath(existingNormalized.RelativePath);
            bool normalizedFileExists = fileSystemProbe.FileExists(normalizedPath);
            logger?.LogInformation(
                $"Project import normalized audio status=existing, artifactId={existingNormalized.Id}, " +
                $"relativePath={existingNormalized.RelativePath}, path={normalizedPath}, " +
                $"exists={normalizedFileExists}, duration={existingNormalized.DurationSeconds:F3}, " +
                $"sampleRate={existingNormalized.SampleRate}, channels={existingNormalized.ChannelCount}.");
            return existingNormalized;
        }

        string fullSourcePath = fileSystemProbe.GetFullPath(mediaAsset.SourceFilePath);
        if (!fileSystemProbe.FileExists(fullSourcePath))
        {
            throw new FileNotFoundException("Source media file was not found for normalized audio extraction.", fullSourcePath);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using ArtifactWriteHandle audioWriteHandle =
            artifactStore.CreateWriteHandle(ProjectArtifactPaths.NormalizedAudioRelativePath);
        AudioExtractionResult extraction;
        try
        {
            extraction = await audioExtractionService.ExtractNormalizedAudioAsync(
                fullSourcePath,
                audioWriteHandle.TemporaryPath,
                cancellationToken,
                ffmpegThreadBudget).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning(
                $"Project import normalized audio extraction failed: source={fullSourcePath}, " +
                $"relativePath={ProjectArtifactPaths.NormalizedAudioRelativePath}, " +
                $"path={artifactStore.GetPath(ProjectArtifactPaths.NormalizedAudioRelativePath)}, " +
                $"error={ex.Message}.",
                ex);
            throw;
        }

        await artifactStore.CommitAsync(audioWriteHandle, cancellationToken).ConfigureAwait(false);

        FileFingerprint audioFingerprint = await fileFingerprintService
            .ComputeAsync(audioWriteHandle.FinalPath, cancellationToken)
            .ConfigureAwait(false);

        var audioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            mediaAsset.ProjectId,
            mediaAsset.Id,
            ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            audioFingerprint.Sha256,
            audioFingerprint.SizeBytes,
            extraction.DurationSeconds,
            extraction.SampleRate,
            extraction.ChannelCount,
            now,
            Provenance: "normalized-audio-refresh:media-spine");

        await mediaAssetRepository.SaveArtifactAsync(audioArtifact, cancellationToken).ConfigureAwait(false);

        string createdNormalizedPath = artifactStore.GetPath(ProjectArtifactPaths.NormalizedAudioRelativePath);
        bool createdNormalizedFileExists = fileSystemProbe.FileExists(createdNormalizedPath);
        logger?.LogInformation(
            $"Project import normalized audio status=created, source={fullSourcePath}, " +
            $"relativePath={ProjectArtifactPaths.NormalizedAudioRelativePath}, path={createdNormalizedPath}, " +
            $"exists={createdNormalizedFileExists}, extractionDuration={extraction.DurationSeconds:F3}, " +
            $"artifactDuration={extraction.DurationSeconds:F3}, sampleRate={extraction.SampleRate}, " +
            $"channels={extraction.ChannelCount}, sizeBytes={audioFingerprint.SizeBytes}.");

        if (!existingArtifacts.Any(static artifact => artifact.Kind == ArtifactKind.WaveformSummary))
        {
            WaveformSummary waveform = await waveformSummaryGenerator
                .GenerateAsync(audioWriteHandle.FinalPath, cancellationToken)
                .ConfigureAwait(false);

            await artifactStore.WriteJsonAsync(
                ProjectArtifactPaths.WaveformSummaryRelativePath,
                waveform,
                cancellationToken).ConfigureAwait(false);

            FileFingerprint waveformFingerprint = await fileFingerprintService
                .ComputeAsync(artifactStore.GetPath(ProjectArtifactPaths.WaveformSummaryRelativePath), cancellationToken)
                .ConfigureAwait(false);

            var waveformArtifact = new ProjectArtifact(
                Guid.NewGuid(),
                mediaAsset.ProjectId,
                mediaAsset.Id,
                ArtifactKind.WaveformSummary,
                ProjectArtifactPaths.WaveformSummaryRelativePath,
                waveformFingerprint.Sha256,
                waveformFingerprint.SizeBytes,
                waveform.DurationSeconds,
                waveform.SampleRate,
                waveform.ChannelCount,
                now,
                Provenance: "waveform-refresh:media-spine");

            await mediaAssetRepository.SaveArtifactAsync(waveformArtifact, cancellationToken).ConfigureAwait(false);
        }

        return audioArtifact;
    }

    public async Task<ProjectArtifact> EnsureStemSeparationAudioAsync(
        MediaAsset mediaAsset,
        IReadOnlyList<ProjectArtifact> existingArtifacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaAsset);
        ArgumentNullException.ThrowIfNull(existingArtifacts);

        ProjectArtifact? existingStemArtifact = existingArtifacts
            .FirstOrDefault(static artifact => artifact.Kind == ArtifactKind.StemSeparationSourceAudio);

        if (existingStemArtifact is not null)
        {
            return existingStemArtifact;
        }

        string fullSourcePath = fileSystemProbe.GetFullPath(mediaAsset.SourceFilePath);
        if (!fileSystemProbe.FileExists(fullSourcePath))
        {
            throw new FileNotFoundException("Source media file was not found for stem separation extraction.", fullSourcePath);
        }

        ArtifactWriteHandle stemWriteHandle = artifactStore.CreateWriteHandle(ProjectArtifactPaths.StemSeparationSourceAudioRelativePath);
        try
        {
            AudioExtractionResult extraction = await audioExtractionService.ExtractStemSeparationAudioAsync(
                fullSourcePath,
                stemWriteHandle.TemporaryPath,
                cancellationToken).ConfigureAwait(false);
            await artifactStore.CommitAsync(stemWriteHandle, cancellationToken).ConfigureAwait(false);

            FileFingerprint stemFingerprint = await fileFingerprintService
                .ComputeAsync(stemWriteHandle.FinalPath, cancellationToken)
                .ConfigureAwait(false);

            var stemArtifact = new ProjectArtifact(
                Guid.NewGuid(),
                mediaAsset.ProjectId,
                mediaAsset.Id,
                ArtifactKind.StemSeparationSourceAudio,
                ProjectArtifactPaths.StemSeparationSourceAudioRelativePath,
                stemFingerprint.Sha256,
                stemFingerprint.SizeBytes,
                extraction.DurationSeconds,
                extraction.SampleRate,
                extraction.ChannelCount,
                DateTimeOffset.UtcNow,
                Provenance: "stem-extraction-refresh:dialogue-isolation-source");

            await mediaAssetRepository.SaveArtifactAsync(stemArtifact, cancellationToken).ConfigureAwait(false);
            return stemArtifact;
        }
        catch
        {
            await stemWriteHandle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RenameProjectAsync(
        RenameProjectRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectName);

        TrackdubProject? project = await projectRepository.GetAsync(cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            throw new InvalidOperationException("Project database does not contain a project record.");
        }

        TrackdubProject originalProject = project;
        ProjectManifest? existingManifest = await artifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            cancellationToken).ConfigureAwait(false);

        string projectName = request.ProjectName.Trim();
        if (!string.Equals(project.Name, projectName, StringComparison.Ordinal))
        {
            project = project with
            {
                Name = projectName,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        await artifactStore.WriteJsonAsync(
            ProjectArtifactPaths.ManifestRelativePath,
            ProjectManifest.FromProject(project, existingManifest?.TranscriptLanguage, existingManifest?.UiSettings),
            cancellationToken).ConfigureAwait(false);

        if (string.Equals(originalProject.Name, project.Name, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await projectRepository.UpdateAsync(project, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await artifactStore.WriteJsonAsync(
                ProjectArtifactPaths.ManifestRelativePath,
                existingManifest ?? ProjectManifest.FromProject(originalProject),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(SourceMediaStatus Status, string? Message)> ResolveSourceStatusAsync(
        SourceMediaReference? sourceReference,
        CancellationToken cancellationToken)
    {
        if (sourceReference is null)
        {
            return (SourceMediaStatus.Unknown, "Source media reference is missing from the project.");
        }

        if (!fileSystemProbe.FileExists(sourceReference.OriginalPath))
        {
            return (
                SourceMediaStatus.Missing,
                $"Source media file was not found at '{sourceReference.OriginalPath}'.");
        }

        FileFingerprint currentFingerprint = await fileFingerprintService.ComputeAsync(
            sourceReference.OriginalPath,
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(currentFingerprint.Sha256, sourceReference.Fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return (
                SourceMediaStatus.Changed,
                $"Source media file contents changed since ingest: '{sourceReference.OriginalPath}'.");
        }

        return (SourceMediaStatus.Available, null);
    }
}
