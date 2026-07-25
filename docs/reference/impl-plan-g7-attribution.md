# Implementation Plan — G7: Export Provenance & Attribution

**Source:** [design-g6-g7-attribution-provenance.md](design-g6-g7-attribution-provenance.md)

---

## Phase 1: Contracts + Domain (1 day)

Attribution types + extend StageRunRecord.

**Files:**
- src/Trackdub.Contracts/Artifacts/StageRunRecord.cs (extend)
- src/Trackdub.Contracts/Export/ExportManifestModel.cs (new)
- src/Trackdub.Contracts/Export/ExportAttributionRequirement.cs (new)

**Logic:**
- StageRunRecord: add ModelAlias? field (nullable, migration-safe)
- ExportManifestModel: Stage, ModelAlias, ModelId?, IsCloud, CloudProviderKey?, License?, RequiresAttribution
- ExportAttributionRequirement: ModelAlias, Stage, License, AttributionText?, SourceUrl?

---

## Phase 2: Application — Catalog (2 days)

Build attribution lookup from manifest.

**Files:**
- src/Trackdub.Application/Export/ModelAttributionCatalog.cs (static)
- src/Trackdub.Contracts/Export/IModelAttributionCatalog.cs

**Logic:**
- Catalog: keyed by model alias (lowercase), populated from bundled-models.manifest.json at compose time
- Find(alias) → ModelAttributionEntry?
- Cloud aliases map to RequiresAttribution=false, License="cloud-service-terms"

---

## Phase 3: Application — Manifest extension (2 days)

Extend ExportManifest + builder.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/ExportManifestModels.cs
- (extend) src/Trackdub.Application/Transcripts/ExportStageHandler.cs

**Logic:**
- ExportManifest: add ContributingModels: IReadOnlyList<ExportManifestModel>, AttributionRequired: IReadOnlyList<ExportAttributionRequirement>
- ExportManifestBuilder.Build(): call BuildContributingModels(request, catalog); filter RequiresAttribution=true → AttributionRequired

---

## Phase 4: Composition — Registration (1 day)

Wire catalog into app.

**Files:**
- src/Trackdub.Composition/CompositionRoot.cs

**Logic:**
- Register IModelAttributionCatalog → ModelAttributionCatalog (populated from manifest at startup)
- Inject into ExportManifestBuilder

---

## Phase 5: Infrastructure — Stage run persistence (1 day)

SQLite migration + write ModelAlias.

**Files:**
- (SQLite migration script)
- (extend) Stage run persistence code

**Logic:**
- Add ModelAlias column to stage_runs table (nullable)
- Extend stage run save to write ModelAlias from execution summary

---

## Phase 6: Composition — Stage handler wiring (1 day)

Pass ModelAlias on stage completion.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/Stages/AsrGenerationStage.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/SpeakerDiarizationStage.cs
- (extend) src/Trackdub.Application/Transcripts/TranslationWorkflow.cs
- (extend) src/Trackdub.Application/Transcripts/TtsWorkflow.cs

**Logic:**
- Each stage handler: extract ModelAlias from StageRuntimeExecutionSummary
- Call StageRunHelper.CompleteAsync(..., modelAlias)

---

## Phase 7: App — Attribution surface (2 days)

Show in export success view.

**Files:**
- (extend) src/Trackdub.App.Avalonia/ViewModels/ExportMixViewModel.cs
- (extend) src/Trackdub.App.Avalonia/Views/ExportMixView.axaml

**Logic:**
- ExportMixViewModel: bind ContributingModels, AttributionRequired
- Show per-stage model summary if any cloud provider used
- Show AttributionRequired section only if HasAttributionRequired=true
- Display license + HF link per model

---

## Tests

- ExportManifestBuilder empty StageModels → ContributingModels has TTS only (backward compat)
- With StageModels: all stages reflected; dedup by (Stage, ModelAlias)
- AttributionRequired contains only RequiresAttribution=true entries
- Cloud alias → IsCloud=true, RequiresAttribution=false, CloudProviderKey set
- ModelAttributionCatalog.Find("kokoro-onnx") → RequiresAttribution=true, Apache-2.0
- ModelAttributionCatalog.Find("whisper-tiny-onnx") → RequiresAttribution=false
- Export no attribution models → AttributionRequired empty; UI section hidden
- SQLite migration: null ModelAlias → manifest omits entry (no crash)
