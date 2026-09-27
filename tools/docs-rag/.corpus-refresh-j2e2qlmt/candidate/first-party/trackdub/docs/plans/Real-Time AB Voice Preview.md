Real-Time A/B Voice Preview - Implementation Plan

Overview

Add instant voice candidate switching during preview, eliminating the need to re-generate entire audio tracks for
comparison.

Phase 1: Extend Domain Layer for Candidate Groups

1.1 Add TtsCandidateGroup record

File: src/Trackdub.Domain/Tts/TtsCandidateGroup.cs (new)

namespace Trackdub.Domain.Tts;

public sealed record TtsCandidateGroup(
    Guid Id,
    Guid ProjectId,
    Guid TranslatedSegmentId,
    int SegmentIndex,
    Guid SelectedCandidateId,
    DateTimeOffset CreatedAtUtc)
{
    public static TtsCandidateGroup Create(
        Guid projectId,
        Guid translatedSegmentId,
        int segmentIndex,
        Guid selectedCandidateId) =>
        new(
            Guid.NewGuid(),
            projectId,
            translatedSegmentId,
            segmentIndex,
            selectedCandidateId,
            DateTimeOffset.UtcNow);

    public TtsCandidateGroup SelectCandidate(Guid candidateId) =>
        this with { SelectedCandidateId = candidateId };
}

1.2 Extend TtsTake with candidate metadata

File: src/Trackdub.Domain/Tts/TtsTake.cs (modify)

Add to the record:

Guid? CandidateGroupId,
int CandidateIndex,  // 0, 1, 2 for A/B/C variants
TtsCandidateVariant Variant  // new enum

Add new enum: File: src/Trackdub.Domain/Tts/TtsCandidateVariant.cs (new)

namespace Trackdub.Domain.Tts;

public enum TtsCandidateVariant
{
    Primary = 0,      // Default/base generation
    Alternative1 = 1, // Slight parameter variation
    Alternative2 = 2  // Different parameter variation
}

1.3 Add repository interface

File: src/Trackdub.Application/Contracts/ITtsCandidateGroupRepository.cs (new)

using Trackdub.Domain.Tts;

namespace Trackdub.Application.Contracts;

public interface ITtsCandidateGroupRepository
{
    Task<TtsCandidateGroup?> GetBySegmentAsync(Guid translatedSegmentId, CancellationToken ct);
    Task SaveAsync(TtsCandidateGroup group, CancellationToken ct);
    Task DeleteAsync(Guid groupId, CancellationToken ct);
}

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Phase 2: Modify TTS Stage to Generate Multiple Candidates

2.1 Add candidate generation configuration

File: src/Trackdub.Application/Transcripts/StartTtsStageHandler.cs (modify)

Add to StartTtsStageRequest:

bool GenerateMultipleCandidates = false,
int CandidateCount = 3  // Generate 3 variants per segment

2.2 Modify SynthesizeSegmentAsync to generate candidates

File: src/Trackdub.Application/Transcripts/StartTtsStageHandler.cs (modify)

In the synthesis loop, when GenerateMultipleCandidates is true:

if (request.GenerateMultipleCandidates)
{
    var candidates = new List<TtsTake>();
    for (int i = 0; i < request.CandidateCount; i++)
    {
        TtsSynthesisRequest candidateRequest = CreateVariantRequest(
            originalRequest,
            i,  // candidate index
            voice);

        TtsTake candidate = await SynthesizeSegmentAsync(
            request,
            stageRunId,
            translatedSegment,
            sourceSegment,
            voice,
            voiceCloneReference,
            reservedArtifactRelativePaths,
            candidateRequest,
            i,
            cancellationToken);

        candidates.Add(candidate);
    }

    // Create candidate group and select first as default
    TtsCandidateGroup group = TtsCandidateGroup.Create(
        request.ProjectId,
        translatedSegment.Id,
        translatedSegment.SegmentIndex,
        candidates[0].Id);

    await candidateGroupRepository.SaveAsync(group, cancellationToken);
    takes.AddRange(candidates);
}

2.3 Add variant request creation

File: src/Trackdub.Application/Transcripts/StartTtsStageHandler.cs (modify)

Add private method:

private TtsSynthesisRequest CreateVariantRequest(
    TtsSynthesisRequest baseRequest,
    int candidateIndex,
    VoiceCatalogEntry voice)
{
    // Vary parameters slightly for each candidate
    // Example: adjust stability, similarity, speed
    var variantOptions = baseRequest.Options with
    {
        // Adjust based on candidateIndex
        // This depends on your TTS engine's parameter support
    };

    return baseRequest with { Options = variantOptions };
}

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Phase 3: Add Candidate Selection and Preview Coordination

3.1 Add candidate selection service

File: src/Trackdub.Application/Transcripts/TtsCandidateSelectionService.cs (new)

using Trackdub.Application.Contracts;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed class TtsCandidateSelectionService
{
    private readonly ITtsCandidateGroupRepository candidateGroupRepository;
    private readonly ITtsTakeRepository ttsTakeRepository;
    private readonly IArtifactStore artifactStore;

    public TtsCandidateSelectionService(
        ITtsCandidateGroupRepository candidateGroupRepository,
        ITtsTakeRepository ttsTakeRepository,
        IArtifactStore artifactStore)
    {
        this.candidateGroupRepository = candidateGroupRepository;
        this.ttsTakeRepository = ttsTakeRepository;
        this.artifactStore = artifactStore;
    }

    public async Task<IReadOnlyList<TtsTake>> GetCandidatesAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        TtsCandidateGroup? group = await candidateGroupRepository
            .GetBySegmentAsync(translatedSegmentId, ct);

        if (group is null)
            return [];

        IReadOnlyList<TtsTake> allTakes = await ttsTakeRepository
            .GetBySegmentAsync(translatedSegmentId, ct);

        return allTakes
            .Where(take => take.CandidateGroupId == group.Id)
            .OrderBy(take => take.CandidateIndex)
            .ToList();
    }

    public async Task<TtsTake?> GetSelectedCandidateAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        TtsCandidateGroup? group = await candidateGroupRepository
            .GetBySegmentAsync(translatedSegmentId, ct);

        if (group is null)
            return null;

        return await ttsTakeRepository.GetByIdAsync(group.SelectedCandidateId, ct);
    }

    public async Task SelectCandidateAsync(
        Guid translatedSegmentId,
        Guid candidateId,
        CancellationToken ct)
    {
        TtsCandidateGroup? group = await candidateGroupRepository
            .GetBySegmentAsync(translatedSegmentId, ct);

        if (group is null)
            throw new InvalidOperationException("No candidate group exists for this segment.");

        TtsCandidateGroup updated = group.SelectCandidate(candidateId);
        await candidateGroupRepository.SaveAsync(updated, ct);
    }

    public async Task<string?> GetSelectedCandidatePathAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        TtsTake? selected = await GetSelectedCandidateAsync(translatedSegmentId, ct);

        if (selected?.ArtifactId is not Guid artifactId)
            return null;

        // You'll need to add a method to get artifact by ID
        // or modify this to use the existing artifact lookup
        return null; // TODO: Implement artifact lookup
    }
}

3.2 Extend preview coordinator for instant switching

File: src/Trackdub.Application/Transcripts/TtsDubPreviewCoordinator.cs (modify)

Add methods:

private readonly TtsCandidateSelectionService? candidateSelectionService;

public TtsDubPreviewCoordinator(
    IAudioPreviewTransport transport,
    IArtifactStore artifactStore,
    TtsCandidateSelectionService? candidateSelectionService = null)
{
    this.transport = transport;
    this.artifactStore = artifactStore;
    this.candidateSelectionService = candidateSelectionService;
}

public async Task SwitchCandidateAsync(
    Guid translatedSegmentId,
    int candidateIndex,
    CancellationToken ct)
{
    if (candidateSelectionService is null)
        return;

    IReadOnlyList<TtsTake> candidates = await candidateSelectionService
        .GetCandidatesAsync(translatedSegmentId, ct);

    if (candidateIndex < 0 || candidateIndex >= candidates.Count)
        return;

    TtsTake selected = candidates[candidateIndex];
    await candidateSelectionService.SelectCandidateAsync(
        translatedSegmentId,
        selected.Id,
        ct);

    // If currently playing this segment, switch instantly
    if (inSequenceMode && sequencePaths.Count > 0)
    {
        // Reload current segment with new candidate
        await ReloadCurrentSegmentAsync(selected, ct);
    }
}

private async Task ReloadCurrentSegmentAsync(TtsTake newTake, CancellationToken ct)
{
    // Get artifact path for new take
    string? artifactPath = await GetArtifactPathAsync(newTake.ArtifactId, ct);
    if (artifactPath is null || !File.Exists(artifactPath))
        return;

    // Stop current playback, reload, resume at same position
    double currentPosition = transport.CurrentPosition;
    await StopCoreAsync(ct);
    await transport.OpenAsync(artifactPath, ct);
    if (currentPosition > 0)
    {
        await transport.SeekAsync(currentPosition, ct);
    }
    await transport.PlayAsync(ct);
}

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Phase 4: Add UI for Real-Time A/B Switching

4.1 Add candidate selector view model

File: src/Trackdub.App.Avalonia/ViewModels/TtsCandidateSelectorViewModel.cs (new)

using System.Collections.ObjectModel;
using System.ComponentModel;
using Trackdub.Application.Transcripts;
using Trackdub.Domain.Tts;

namespace Trackdub.App.Avalonia.ViewModels;

public sealed class TtsCandidateSelectorViewModel : ObservableObject
{
    private readonly TtsCandidateSelectionService selectionService;
    private readonly TtsDubPreviewCoordinator previewCoordinator;
    private int selectedCandidateIndex;
    private Guid translatedSegmentId;
    private bool hasCandidates;

    public TtsCandidateSelectorViewModel(
        TtsCandidateSelectionService selectionService,
        TtsDubPreviewCoordinator previewCoordinator)
    {
        this.selectionService = selectionService;
        this.previewCoordinator = previewCoordinator;
        Candidates = new ObservableCollection<TtsCandidateViewModel>();
    }

    public ObservableCollection<TtsCandidateViewModel> Candidates { get; }

    public int SelectedCandidateIndex
    {
        get => selectedCandidateIndex;
        set
        {
            if (SetProperty(ref selectedCandidateIndex, value))
            {
                _ = SwitchToCandidateAsync(value);
            }
        }
    }

    public bool HasCandidates
    {
        get => hasCandidates;
        private set => SetProperty(ref hasCandidates, value);
    }

    public async Task LoadCandidatesAsync(Guid segmentId, CancellationToken ct)
    {
        translatedSegmentId = segmentId;
        Candidates.Clear();

        IReadOnlyList<TtsTake> candidates = await selectionService
            .GetCandidatesAsync(segmentId, ct);

        for (int i = 0; i < candidates.Count; i++)
        {
            Candidates.Add(new TtsCandidateViewModel(
                i,
                candidates[i],
                i == 0)); // First is default
        }

        HasCandidates = candidates.Count > 0;
    }

    private async Task SwitchToCandidateAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= Candidates.Count)
            return;

        await selectionService.SelectCandidateAsync(
            translatedSegmentId,
            Candidates[index].TakeId,
            ct);

        await previewCoordinator.SwitchCandidateAsync(
            translatedSegmentId,
            index,
            ct);
    }
}

public sealed record TtsCandidateViewModel(
    int Index,
    TtsTake Take,
    bool IsDefault)
{
    public string Label => IsDefault ? "Default" : $"Variant {Index}";
    public Guid TakeId => Take.Id;
    public string? VoiceId => Take.VoiceId;
    public TtsCandidateVariant Variant => Take.Variant;
}

4.2 Add keyboard shortcut handling

File: src/Trackdub.App.Avalonia/ViewModels/PreviewMixViewModel.cs (modify)

Add:

private readonly TtsCandidateSelectorViewModel candidateSelector;

// In constructor:
candidateSelector = new TtsCandidateSelectorViewModel(
    selectionService,
    previewCoordinator);

// Add method:
public async Task HandleCandidateShortcutAsync(int candidateIndex, CancellationToken ct)
{
    if (candidateIndex >= 0 && candidateIndex < candidateSelector.Candidates.Count)
    {
        candidateSelector.SelectedCandidateIndex = candidateIndex;
    }
}

4.3 Add XAML UI for candidate selector

File: src/Trackdub.App.Avalonia/Views/PreviewMixView.xaml (modify)

Add to the preview panel:

<StackPanel Orientation="Horizontal" Margin="0,8,0,0"
            Visibility="{Binding CandidateSelector.HasCandidates, Converter={StaticResource BoolToVisibilityConverte
r}}">
    <TextBlock Text="Voice Candidates:" VerticalAlignment="Center" Margin="0,0,8,0"/>
    <ItemsControl ItemsSource="{Binding CandidateSelector.Candidates}">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
                <StackPanel Orientation="Horizontal"/>
            </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <Button Content="{Binding Label}"
                        Command="{Binding DataContext.SelectCandidateCommand, RelativeSource={RelativeSource AncestorType=Page}}"
                        CommandParameter="{Binding Index}"
                        Margin="0,0,4,0"
                        Padding="8,4"/>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
    <TextBlock Text="[1] [2] [3] to switch"
               Foreground="Gray"
               VerticalAlignment="Center"
               Margin="8,0,0,0"
               FontSize="11"/>
</StackPanel>

4.4 Add keyboard handler in main window

File: src/Trackdub.App.Avalonia/MainWindow.xaml.cs (modify)

protected override void OnKeyDown(KeyRoutedEventArgs e)
{
    base.OnKeyDown(e);

    // Handle 1, 2, 3 keys for candidate switching
    if (e.Key == VirtualKey.Number1)
    {
        _ = previewMixViewModel.HandleCandidateShortcutAsync(0, CancellationToken.None);
        e.Handled = true;
    }
    else if (e.Key == VirtualKey.Number2)
    {
        _ = previewMixViewModel.HandleCandidateShortcutAsync(1, CancellationToken.None);
        e.Handled = true;
    }
    else if (e.Key == VirtualKey.Number3)
    {
        _ = previewMixViewModel.HandleCandidateShortcutAsync(2, CancellationToken.None);
        e.Handled = true;
    }
}

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Phase 5: Add Persistence for Locked-in Selections

5.1 Implement candidate group repository

File: src/Trackdub.Infrastructure/Repositories/TtsCandidateGroupRepository.cs (new)

using Dapper;
using Trackdub.Application.Contracts;
using Trackdub.Domain.Tts;
using Trackdub.Infrastructure.Database;

namespace Trackdub.Infrastructure.Repositories;

public sealed class TtsCandidateGroupRepository : ITtsCandidateGroupRepository
{
    private readonly IDbConnectionFactory dbConnectionFactory;

    public TtsCandidateGroupRepository(IDbConnectionFactory dbConnectionFactory)
    {
        this.dbConnectionFactory = dbConnectionFactory;
    }

    public async Task<TtsCandidateGroup?> GetBySegmentAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT * FROM tts_candidate_groups
            WHERE translated_segment_id = @TranslatedSegmentId";

        using var connection = dbConnectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<TtsCandidateGroup>(
            sql,
            new { TranslatedSegmentId = translatedSegmentId });
    }

    public async Task SaveAsync(TtsCandidateGroup group, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO tts_candidate_groups
                (id, project_id, translated_segment_id, segment_index, selected_candidate_id, created_at_utc)
            VALUES
                (@Id, @ProjectId, @TranslatedSegmentId, @SegmentIndex, @SelectedCandidateId, @CreatedAtUtc)
            ON CONFLICT (translated_segment_id)
            DO UPDATE SET
                selected_candidate_id = @SelectedCandidateId";

        using var connection = dbConnectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, group);
    }

    public async Task DeleteAsync(Guid groupId, CancellationToken ct)
    {
        const string sql = "DELETE FROM tts_candidate_groups WHERE id = @GroupId";

        using var connection = dbConnectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, new { GroupId = groupId });
    }
}

5.2 Add database migration

File: src/Trackdub.Infrastructure/Database/Migrations/xxxx_add_tts_candidate_groups.sql (new)

CREATE TABLE IF NOT EXISTS tts_candidate_groups (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL,
    translated_segment_id TEXT NOT NULL UNIQUE,
    segment_index INTEGER NOT NULL,
    selected_candidate_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
    FOREIGN KEY (selected_candidate_id) REFERENCES tts_takes(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_tts_candidate_groups_segment
    ON tts_candidate_groups(translated_segment_id);

CREATE INDEX IF NOT EXISTS idx_tts_candidate_groups_project
    ON tts_candidate_groups(project_id);

5.3 Extend TTS takes table

File: src/Trackdub.Infrastructure/Database/Migrations/xxxx_add_candidate_metadata_to_tts_takes.sql (new)

ALTER TABLE tts_takes ADD COLUMN candidate_group_id TEXT;
ALTER TABLE tts_takes ADD COLUMN candidate_index INTEGER DEFAULT 0;
ALTER TABLE tts_takes ADD COLUMN candidate_variant INTEGER DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_tts_takes_candidate_group
    ON tts_takes(candidate_group_id);

5.4 Register services in DI

File: src/Trackdub.Composition/ServiceCollectionExtensions.cs (modify)

services.AddSingleton<ITtsCandidateGroupRepository, TtsCandidateGroupRepository>();
services.AddSingleton<TtsCandidateSelectionService>();

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Testing Strategy

Unit Tests

  1. Test TtsCandidateGroup creation and candidate selection
  2. Test TtsCandidateSelectionService candidate retrieval and selection
  3. Test variant request generation with different parameters

Integration Tests

  1. Test TTS stage with GenerateMultipleCandidates = true
  2. Test candidate group repository CRUD operations
  3. Test preview coordinator candidate switching

Manual Testing

  1. Generate a project with multiple candidates
  2. Play preview and press 1, 2, 3 to switch voices
  3. Verify selection persists after app restart
  4. Verify export uses selected candidates

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Rollout Plan

Phase 1 (Foundation)

  • Implement Phase 1 (domain layer)
  • Add database migrations
  • Write unit tests

Phase 2 (Backend)

  • Implement Phase 2 (TTS stage modification)
  • Implement Phase 3 (selection service)
  • Write integration tests

Phase 3 (Frontend)

  • Implement Phase 4 (UI and keyboard shortcuts)
  • Manual testing with real content

Phase 4 (Persistence)

  • Implement Phase 5 (repository and DI)
  • End-to-end testing

Phase 5 (Polish)

  • Add visual feedback for active candidate
  • Add candidate comparison metrics (duration, quality score)
  • Add "Generate Candidates" button to existing projects

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Estimated Effort

  • Phase 1: 2-3 days (domain + migrations)
  • Phase 2: 3-4 days (TTS stage + selection service)
  • Phase 3: 2-3 days (preview coordination)
  • Phase 4: 3-4 days (UI + keyboard handling)
  • Phase 5: 1-2 days (persistence + DI)

Total: ~11-16 days for a complete implementation

────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

Success Metrics

  1. Time savings: Users can compare 3 voice variants in under 1 minute vs. 15+ minutes currently
  2. Adoption: >50% of users with candidate generation enabled use the feature within first week
  3. Quality: Users report higher satisfaction with final voice selection
  4. Performance: Candidate switching happens within 100ms (imperceptible delay)


