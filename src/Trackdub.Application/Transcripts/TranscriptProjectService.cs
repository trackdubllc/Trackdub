using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Transcripts;

[Obsolete("Use TranscriptWorkspace workflow coordinators. This adapter exists only for transitional compatibility.")]
public sealed class TranscriptProjectService(TranscriptWorkspace workspace)
{
    private readonly TranscriptWorkspace workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    public Task<TranscriptProjectState> CreateAsync(
        CreateTranscriptProjectRequest request,
        CancellationToken cancellationToken,
        IProgress<StemSeparationProgress>? stemSeparationProgress = null) =>
        workspace.CreateProjectAsync(request, cancellationToken, stemSeparationProgress);

    public Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        workspace.Project.OpenAsync(cancellationToken);

    public RequiredDiarizationModelStatus? GetRequiredDiarizationModelStatus() =>
        workspace.DiarizationModels.GetRequiredDiarizationModelStatus();

    public Task DownloadRequiredDiarizationModelAsync(CancellationToken cancellationToken) =>
        workspace.DiarizationModels.DownloadRequiredDiarizationModelAsync(cancellationToken);

    public Task ImportDiarizationModelAsync(
        string sourceModelPath,
        CancellationToken cancellationToken) =>
        workspace.DiarizationModels.ImportDiarizationModelAsync(sourceModelPath, cancellationToken);

    public Task<TranscriptProjectState> SelectTranslationTargetAsync(
        SetTranslationTargetRequest request,
        CancellationToken cancellationToken) =>
        workspace.Translation.SelectTranslationTargetAsync(request, cancellationToken);

    public Task<TranscriptProjectState> RelocateSourceAsync(
        RelocateTranscriptSourceRequest request,
        CancellationToken cancellationToken) =>
        workspace.RelocateSourceAsync(request, cancellationToken);

    public Task<TranscriptProjectState> RunStemSeparationAsync(
        CancellationToken cancellationToken,
        IProgress<StemSeparationProgress>? progress = null,
        string? preferredModelAlias = null,
        InferenceModelPreferences? modelPreferences = null,
        bool regenerateTranscript = true) =>
        workspace.RunStemSeparationAsync(
            cancellationToken,
            progress,
            preferredModelAlias,
            modelPreferences,
            regenerateTranscript);

    public Task<TranscriptProjectState> SaveEditsAsync(
        SaveTranscriptEditsRequest request,
        CancellationToken cancellationToken) =>
        workspace.SaveTranscriptEditsAsync(request, cancellationToken);

    public Task<TranscriptProjectState> SplitSegmentAsync(
        SplitTranscriptSegmentRequest request,
        CancellationToken cancellationToken) =>
        workspace.SplitSegmentAsync(request, cancellationToken);

    public Task<TranscriptProjectState> MergeSegmentsAsync(
        MergeTranscriptSegmentsRequest request,
        CancellationToken cancellationToken) =>
        workspace.MergeSegmentsAsync(request, cancellationToken);

    public Task<TranscriptProjectState> MergeSegmentRunAsync(
        MergeTranscriptSegmentRunRequest request,
        CancellationToken cancellationToken) =>
        workspace.MergeSegmentRunAsync(request, cancellationToken);

    public Task<TranscriptProjectState> TrimSegmentAsync(
        TrimTranscriptSegmentRequest request,
        CancellationToken cancellationToken) =>
        workspace.TrimSegmentAsync(request, cancellationToken);

    public Task<TranscriptProjectState> DeleteSegmentAsync(
        DeleteTranscriptSegmentRequest request,
        CancellationToken cancellationToken) =>
        workspace.DeleteSegmentAsync(request, cancellationToken);

    public Task<TranscriptProjectState> RetranscribeSegmentsAsync(
        RetranscribeTranscriptSegmentsRequest request,
        CancellationToken cancellationToken) =>
        workspace.RetranscribeSegmentsAsync(request, cancellationToken);

    public Task<TranscriptProjectState> RerunDiarizationAsync(
        RerunDiarizationRequest request,
        CancellationToken cancellationToken) =>
        workspace.RerunDiarizationAsync(request, cancellationToken);

    public Task<TranscriptProjectState> SetTranscriptLanguageAsync(
        SetTranscriptLanguageRequest request,
        CancellationToken cancellationToken) =>
        workspace.SetTranscriptLanguageAsync(request, cancellationToken);

    public Task<TranscriptProjectState> GenerateTranslationAsync(
        GenerateTranslationRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null) =>
        workspace.GenerateTranslationAsync(request, cancellationToken, progress);

    public Task<TranscriptProjectState> RetranslateSegmentAsync(
        RetranslateSegmentRequest request,
        CancellationToken cancellationToken) =>
        workspace.RetranslateSegmentAsync(request, cancellationToken);

    public Task<TranscriptProjectState> SaveTranslationEditsAsync(
        SaveTranslationEditsRequest request,
        CancellationToken cancellationToken) =>
        workspace.SaveTranslationEditsAsync(request, cancellationToken);

    public Task<TranscriptProjectState> RenameSpeakerAsync(
        RenameSpeakerRequest request,
        CancellationToken cancellationToken) =>
        workspace.RenameSpeakerAsync(request, cancellationToken);

    public Task<TranscriptProjectState> MergeSpeakersAsync(
        MergeSpeakersRequest request,
        CancellationToken cancellationToken) =>
        workspace.MergeSpeakersAsync(request, cancellationToken);

    public Task<TranscriptProjectState> AssignVoiceToSpeakerAsync(
        AssignVoiceToSpeakerRequest request,
        CancellationToken cancellationToken) =>
        workspace.AssignVoiceToSpeakerAsync(request, cancellationToken);

    public Task<TranscriptProjectState> GenerateTtsForSpeakerAsync(
        GenerateTtsForSpeakerRequest request,
        CancellationToken cancellationToken) =>
        workspace.GenerateTtsForSpeakerAsync(request, cancellationToken);

    public Task<TranscriptProjectState> GenerateTtsForSegmentAsync(
        GenerateTtsForSegmentRequest request,
        CancellationToken cancellationToken) =>
        workspace.GenerateTtsForSegmentAsync(request, cancellationToken);

    public Task<TranscriptProjectState> GenerateTtsForAllSpeakersAsync(
        GenerateTtsForAllSpeakersRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null) =>
        workspace.GenerateTtsForAllSpeakersAsync(request, cancellationToken, progress);

    public Task<PreviewVoiceResult> PreviewVoiceAsync(
        PreviewVoiceRequest request,
        CancellationToken cancellationToken) =>
        workspace.Voices.PreviewVoiceAsync(request, cancellationToken);

    public Task<TranscriptProjectState> RestoreEditingStateAsync(
        RestoreEditingStateRequest request,
        CancellationToken cancellationToken) =>
        workspace.RestoreEditingStateAsync(request, cancellationToken);

    public Task<TranscriptProjectState> RegenerateStaleTtsForSpeakerAsync(
        RegenerateStaleTtsForSpeakerRequest request,
        CancellationToken cancellationToken) =>
        workspace.RegenerateStaleTtsForSpeakerAsync(request, cancellationToken);

    public Task<TranscriptProjectState> StretchTtsTakeAsync(
        StretchTtsTakeRequest request,
        CancellationToken cancellationToken) =>
        workspace.StretchTtsTakeAsync(request, cancellationToken);

    public Task<TranscriptProjectState> AssignSpeakerToSegmentAsync(
        AssignSpeakerToSegmentRequest request,
        CancellationToken cancellationToken) =>
        workspace.AssignSpeakerToSegmentAsync(request, cancellationToken);

    public Task<TranscriptProjectState> AssignSpeakerToSegmentsAsync(
        AssignSpeakerToSegmentsRequest request,
        CancellationToken cancellationToken) =>
        workspace.AssignSpeakerToSegmentsAsync(request, cancellationToken);

    public Task<TranscriptProjectState> CreateSpeakerFromSegmentsAsync(
        CreateSpeakerFromSegmentsRequest request,
        CancellationToken cancellationToken) =>
        workspace.CreateSpeakerFromSegmentsAsync(request, cancellationToken);

    public Task<TranscriptProjectState> SplitSpeakerTurnAsync(
        SplitSpeakerTurnRequest request,
        CancellationToken cancellationToken) =>
        workspace.SplitSpeakerTurnAsync(request, cancellationToken);

    public Task<TranscriptProjectState> ExtractReferenceClipAsync(
        ExtractReferenceClipRequest request,
        CancellationToken cancellationToken) =>
        workspace.ExtractReferenceClipAsync(request, cancellationToken);
}
