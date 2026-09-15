using Trackdub.Application.Dubbing;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Tests;

public sealed class DubbingPipelineEngineTests
{
    [Fact]
    public async Task RunStageWorkflowAsync_unknown_stage_fails_with_unsupported_reason()
    {
        var session = new FakeSession();
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "source.mp4",
            TargetLanguageCode = "es",
        };
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            new Dictionary<string, ExecutionProviderKind>());

        DubbingPipelineEngine.StageWorkflowResult result =
            await DubbingPipelineEngine.RunStageWorkflowAsync(
                session,
                options,
                "bogus-stage",
                selections,
                progress: null,
                CancellationToken.None);

        Assert.Equal(StageStatus.Failed, result.Status);
        Assert.Equal("STAGE_UNSUPPORTED", result.ReasonCode);
    }

    [Fact]
    public void ExtendedStageOrder_contains_shared_utility_stages_in_pipeline_position()
    {
        IReadOnlyList<string> order = DubbingPipelineStages.ExtendedStageOrder;

        Assert.Contains(StageNames.AudioPreparation, order);
        Assert.Contains(StageNames.TextRefinementAsr, order);
        Assert.Contains(StageNames.OverlapRescue, order);

        Assert.True(IndexOf(order, StageNames.AudioPreparation) < IndexOf(order, StageNames.Vad));
        Assert.True(IndexOf(order, StageNames.OverlapRescue) > IndexOf(order, StageNames.Diarization));
        Assert.True(IndexOf(order, StageNames.TextRefinementAsr) > IndexOf(order, StageNames.Asr));
        Assert.True(IndexOf(order, StageNames.TextRefinementAsr) < IndexOf(order, StageNames.Translation));
    }

    [Fact]
    public void ApplyModelPreferenceAliases_pins_explicit_alias_over_ui_selection()
    {
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            new Dictionary<string, ExecutionProviderKind>(),
            AsrModelAlias: "whisper-ui-pick",
            TtsModelAlias: "kokoro-ui-pick");
        var preferences = new InferenceModelPreferences(TtsModelAlias: "chatterbox-clone-pin");

        RuntimeModelSelections merged = DubbingPipelineEngine.ApplyModelPreferenceAliases(selections, preferences);

        Assert.Equal("chatterbox-clone-pin", merged.TtsModelAlias);
        Assert.Equal("whisper-ui-pick", merged.AsrModelAlias);
    }

    [Fact]
    public void ApplyModelPreferenceAliases_with_null_preferences_keeps_host_selections()
    {
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            new Dictionary<string, ExecutionProviderKind>(),
            TtsModelAlias: "kokoro-ui-pick");

        RuntimeModelSelections merged = DubbingPipelineEngine.ApplyModelPreferenceAliases(selections, null);

        Assert.Equal("kokoro-ui-pick", merged.TtsModelAlias);
    }

    private static int IndexOf(IReadOnlyList<string> order, string stageName)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], stageName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class FakeSession : IDubbingSession
    {
        public string ProjectRootPath => ".";

        // Only consumed by recognized stages; the unknown-stage arm never dereferences it.
        public TranscriptWorkspace Workspace => null!;

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
