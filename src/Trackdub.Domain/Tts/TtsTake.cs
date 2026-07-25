namespace Trackdub.Domain.Tts;

public sealed record TtsTake(
    Guid Id,
    Guid ProjectId,
    Guid VoiceAssignmentId,
    Guid? TranslatedSegmentId,
    int SegmentIndex,
    string? TranslatedTextHash,
    Guid? ArtifactId,
    Guid? StageRunId,
    TtsTakeStatus Status,
    bool IsStale,
    int? DurationSamples,
    int? SampleRate,
    string? Provider,
    string? ModelId,
    string? VoiceId,
    TtsTakeKind Kind,
    Guid? ReferenceClipArtifactId,
    double? DurationOverrunRatio,
    double? PreStretchDurationSeconds,
    double? StretchRatioApplied,
    TtsStretchMode StretchMode,
    TtsStretchEngine StretchEngine,
    DateTimeOffset CreatedAtUtc,
    string? InputFingerprint = null,
    Guid? CandidateGroupId = null,
    int? CandidateIndex = null,
    TtsCandidateVariant Variant = TtsCandidateVariant.Primary)
{
    public static TtsTake Create(
        Guid projectId,
        Guid voiceAssignmentId,
        Guid? translatedSegmentId = null,
        int segmentIndex = 0,
        string? translatedTextHash = null,
        string? inputFingerprint = null) =>
        CreateStock(projectId, voiceAssignmentId, translatedSegmentId, segmentIndex, translatedTextHash, inputFingerprint);

    public static TtsTake CreateStock(
        Guid projectId,
        Guid voiceAssignmentId,
        Guid? translatedSegmentId = null,
        int segmentIndex = 0,
        string? translatedTextHash = null,
        string? inputFingerprint = null) =>
        CreateCore(
            projectId,
            voiceAssignmentId,
            translatedSegmentId,
            segmentIndex,
            translatedTextHash,
            TtsTakeKind.Stock,
            referenceClipArtifactId: null,
            inputFingerprint);

    public static TtsTake CreateVoiceCloned(
        Guid projectId,
        Guid voiceAssignmentId,
        Guid referenceClipArtifactId,
        Guid? translatedSegmentId = null,
        int segmentIndex = 0,
        string? translatedTextHash = null,
        string? inputFingerprint = null)
    {
        if (referenceClipArtifactId == Guid.Empty)
        {
            throw new ArgumentException("Reference clip artifact id is required.", nameof(referenceClipArtifactId));
        }

        return CreateCore(
            projectId,
            voiceAssignmentId,
            translatedSegmentId,
            segmentIndex,
            translatedTextHash,
            TtsTakeKind.VoiceCloned,
            referenceClipArtifactId,
            inputFingerprint);
    }

    private static TtsTake CreateCore(
        Guid projectId,
        Guid voiceAssignmentId,
        Guid? translatedSegmentId,
        int segmentIndex,
        string? translatedTextHash,
        TtsTakeKind kind,
        Guid? referenceClipArtifactId,
        string? inputFingerprint)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (voiceAssignmentId == Guid.Empty)
        {
            throw new ArgumentException("Voice assignment id is required.", nameof(voiceAssignmentId));
        }

        if (segmentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex), "Segment index cannot be negative.");
        }

        return new TtsTake(
            Guid.NewGuid(),
            projectId,
            voiceAssignmentId,
            translatedSegmentId,
            segmentIndex,
            string.IsNullOrWhiteSpace(translatedTextHash) ? null : translatedTextHash.Trim(),
            ArtifactId: null,
            StageRunId: null,
            TtsTakeStatus.Pending,
            IsStale: false,
            DurationSamples: null,
            SampleRate: null,
            Provider: null,
            ModelId: null,
            VoiceId: null,
            kind,
            referenceClipArtifactId,
            DurationOverrunRatio: null,
            PreStretchDurationSeconds: null,
            StretchRatioApplied: null,
            TtsStretchMode.None,
            TtsStretchEngine.None,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(inputFingerprint) ? null : inputFingerprint.Trim(),
            CandidateGroupId: null,
            CandidateIndex: null,
            Variant: TtsCandidateVariant.Primary);
    }

    public TtsTake MarkStale() =>
        ClearStretchMetadata() with { IsStale = true, Status = TtsTakeStatus.Stale };

    public TtsTake Complete(Guid artifactId, int durationSamples, int sampleRate, string provider) =>
        Complete(
            artifactId,
            StageRunId,
            durationSamples,
            sampleRate,
            provider,
            ModelId,
            VoiceId,
            DurationOverrunRatio,
            PreStretchDurationSeconds,
            StretchRatioApplied,
            StretchMode,
            StretchEngine);

    public TtsTake Complete(
        Guid artifactId,
        Guid? stageRunId,
        int durationSamples,
        int sampleRate,
        string provider,
        string? modelId,
        string? voiceId,
        double? durationOverrunRatio,
        double? preStretchDurationSeconds = null,
        double? stretchRatioApplied = null,
        TtsStretchMode stretchMode = TtsStretchMode.None,
        TtsStretchEngine stretchEngine = TtsStretchEngine.None) =>
        this with
        {
            ArtifactId = artifactId,
            StageRunId = stageRunId,
            DurationSamples = durationSamples,
            SampleRate = sampleRate,
            Provider = provider,
            ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim(),
            VoiceId = string.IsNullOrWhiteSpace(voiceId) ? null : voiceId.Trim(),
            DurationOverrunRatio = durationOverrunRatio,
            PreStretchDurationSeconds = preStretchDurationSeconds,
            StretchRatioApplied = stretchRatioApplied,
            StretchMode = stretchMode,
            StretchEngine = stretchEngine,
            Status = TtsTakeStatus.Completed,
            IsStale = false
        };

    public TtsTake ApplyStretch(
        TtsStretchMode mode,
        TtsStretchEngine engine,
        double ratio,
        double preStretchDurationSeconds,
        int durationSamples,
        double? durationOverrunRatio)
    {
        if (mode is TtsStretchMode.None)
        {
            throw new ArgumentException("Stretch mode must describe an applied stretch.", nameof(mode));
        }

        if (engine is TtsStretchEngine.None)
        {
            throw new ArgumentException("Stretch engine must describe an applied stretch.", nameof(engine));
        }

        if (!double.IsFinite(ratio) || ratio <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), "Stretch ratio must be positive.");
        }

        if (!double.IsFinite(preStretchDurationSeconds) || preStretchDurationSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(preStretchDurationSeconds), "Pre-stretch duration must be positive.");
        }

        if (durationSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSamples), "Duration samples must be positive.");
        }

        return this with
        {
            DurationSamples = durationSamples,
            DurationOverrunRatio = durationOverrunRatio,
            PreStretchDurationSeconds = preStretchDurationSeconds,
            StretchRatioApplied = ratio,
            StretchMode = mode,
            StretchEngine = engine,
            Status = TtsTakeStatus.Completed,
            IsStale = false
        };
    }

    public TtsTake ClearStretchMetadata() =>
        this with
        {
            PreStretchDurationSeconds = null,
            StretchRatioApplied = null,
            StretchMode = TtsStretchMode.None,
            StretchEngine = TtsStretchEngine.None
        };

    // Failed is a terminal status; clearing IsStale avoids the ambiguous
    // "Failed but also stale" combination and matches how Complete() behaves.
    public TtsTake Fail() =>
        ClearStretchMetadata() with { Status = TtsTakeStatus.Failed, IsStale = false };
}
