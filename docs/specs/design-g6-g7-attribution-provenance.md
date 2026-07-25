# Design Spec — G6 (closed by G3) / G7: Export Provenance & Attribution

## G6 — status: closed by G3

Gap G6: "Cloud-TTS egress unflagged, separate from clone consent."

G3 spec ([design-g3-cloud-egress-visibility.md](design-g3-cloud-egress-visibility.md)) closes this completely. G3's `CloudEgressConsentDialog` + consent keys `text:elevenlabs`, `text:openai`, `text:google` are exactly the per-provider consent gate G6 requires. No additional spec needed for G6.

---

# G7 — Export Provenance & Attribution

**Source gap:** [service-blueprint-first-dub.md](service-blueprint-first-dub.md) · Gap **G7** (Low) — no provenance/attribution in the exported result. Several bundled models require attribution; cloud providers have their own terms. The export doesn't tell the user what was used or what attribution they owe.

---

## 1. Problem — what's missing

`ExportManifest` already captures:

| Field | What's in it |
|---|---|
| `ModelIds` | Distinct TTS model IDs from `TtsTakes.ModelId` **only** |
| `TtsVoices` | Distinct voice IDs from `TtsTakes.VoiceId` |
| `StageRunIds` | Run IDs for transcript + translation + selected TTS takes |
| Per-segment `ModelId`, `VoiceId` | TTS model/voice per segment |

**Not captured:**
- ASR model ID (Whisper variant, Qwen3-ASR, OpenAI Whisper Cloud, etc.)
- Translation engine (opus-mt model, Madlad, DeepL, OpenAI GPT, Gemini)
- Separation model (spleeter, mrx-cocktail-fork)
- Diarization model (sortformer)
- Cloud providers actually used during the run
- Attribution requirements for any model

**Models with `requires_attribution: true` in bundled manifest (confirmed):**

| Model ID | Stage | License |
|---|---|---|
| `csukuangfj/sherpa-onnx-spleeter-2stems` | Separation | MIT |
| `tonythethompson/mrx-cocktail-fork-onnx` | Separation | MIT |
| `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` | Diarization | NVIDIA-Open-Model-License |
| `onnx-community/Kokoro-82M-v1.0-ONNX` | TTS | Apache-2.0 |
| `onnx-community/opus-mt-*` family | Translation | CC-BY-4.0 |

**Models with `requires_attribution: false`:** Whisper family, Silero VAD, Qwen3-ASR, Qwen2.5 — these need no attribution.

**Result:** a user who exports with spleeter + sortformer + kokoro has attribution obligations they cannot see. Cloud providers (DeepL, ElevenLabs, etc.) require their own disclosures. The export manifest and UI are silent on all of this.

---

## 2. Goals / Non-goals

**Goals**
- Export manifest records **all** contributing models (all stages), not just TTS.
- Attribution requirements for `requires_attribution: true` models are surfaced in the manifest and in the export success UI.
- Cloud providers used are recorded (matched from G3 consent keys — no new egress logic here).
- Export sidecar JSON (`{filename}.export-manifest.json`) is the single source of truth for compliance.

**Non-goals**
- Generating attribution text prose — spec records structured data (model ID, license, attribution URL); rendering human prose is a UI concern.
- Legal review of attribution requirements — spec surfaces what the manifest says; legal interpretation is out of scope.
- Provenance for preview mixes — only the final export.
- Backfilling old exports.

---

## 3. Per-stage model capture — where the data lives

Each stage has a `StageRunRecord` and optionally a `StageRuntimeExecutionSummary` (via `IStageRuntimeExecutionReporter`). The summary carries:

```csharp
StageRuntimeExecutionSummary(
    string RequestedProvider,
    string SelectedProvider,
    string ModelAlias,         // ← the model that actually ran
    string BootstrapDetail)
```

`ModelAlias` maps 1:1 to a manifest entry (e.g. `"whisper-tiny-onnx"`, `"sortformer-4spk"`, `"kokoro-onnx"`, `"deepl-cloud"`).

**Collection point:** `ExportStageHandler.GetContributingStageRuns()` already selects the `StageRunRecord` list. Extend it (or its callsite) to also collect `ModelAlias` per stage run.

**Persistence:** `StageRunRecord` currently stores `Status`, `StartedAtUtc`, `CompletedAtUtc` etc. Extend to include `ModelAlias`/`ProviderAlias` or retrieve from a `StageRunDetail` store. See §6 for the least-invasive hook.

---

## 4. `ExportManifest` extension

Add two new top-level fields:

```csharp
public sealed record ExportManifest(
    // ... existing fields unchanged ...

    /// <summary>All models that contributed to this export, across all stages.</summary>
    IReadOnlyList<ExportManifestModel> ContributingModels,

    /// <summary>Attribution requirements derived from contributing models with requires_attribution=true.</summary>
    IReadOnlyList<ExportAttributionRequirement> AttributionRequired);
```

### `ExportManifestModel`

```csharp
public sealed record ExportManifestModel(
    string Stage,              // "asr" | "translation" | "tts" | "separation" | "diarization"
    string ModelAlias,         // "whisper-tiny-onnx", "deepl-cloud", "kokoro-onnx", ...
    string? ModelId,           // full HF model id if local; null for cloud
    bool IsCloud,              // true for deepl-cloud, openai-*, etc.
    string? CloudProviderKey,  // "deepl" | "openai" | "gemini" | "elevenlabs" | "google" | null
    string? License,           // "MIT" | "Apache-2.0" | "CC-BY-4.0" | null (unknown for cloud)
    bool RequiresAttribution); // from bundled manifest; false for cloud engines
```

### `ExportAttributionRequirement`

```csharp
public sealed record ExportAttributionRequirement(
    string ModelAlias,
    string Stage,
    string License,
    string? AttributionText,   // "sortformer by NVIDIA, licensed under NVIDIA-Open-Model-License"
    string? SourceUrl);        // HF model page or project URL
```

---

## 5. Attribution catalog

Static catalog in `Trackdub.Application` (no inference dependencies). Populated from `bundled-models.manifest.json` at build/compose time. The manifest already has `requires_attribution`, `license`, `source_url`, `requires_user_consent` — all needed fields are present.

```csharp
public static class ModelAttributionCatalog
{
    // Keyed by model alias (normalized lowercase)
    private static readonly IReadOnlyDictionary<string, ModelAttributionEntry> _entries;

    public static ModelAttributionEntry? Find(string modelAlias) =>
        _entries.TryGetValue(modelAlias.ToLowerInvariant(), out var entry) ? entry : null;

    public static bool RequiresAttribution(string modelAlias) =>
        Find(modelAlias) is { RequiresAttribution: true };
}

public sealed record ModelAttributionEntry(
    string ModelId,
    string ModelAlias,
    string License,
    bool RequiresAttribution,
    string? SourceUrl,
    string? AttributionText);
```

`ModelAttributionCatalog` is initialized from the manifest at composition time (injected as `IModelAttributionCatalog` so it's testable). Cloud engine aliases (`deepl-cloud`, `openai-tts-cloud`, etc.) map to `RequiresAttribution: false` with `License: "cloud-service-terms"`.

---

## 6. Data collection — `ExportManifestBuildRequest` extension

Extend `ExportManifestBuildRequest` with a new field:

```csharp
IReadOnlyList<StageModelRecord>? StageModels = null
```

```csharp
public sealed record StageModelRecord(
    string StageName,
    string ModelAlias,
    bool IsCloud,
    string? CloudProviderKey);
```

**Where to populate it:** `ExportStageHandler.BuildManifest()` calls `ExportManifestBuilder.Build(request)`. The handler has `currentState` which includes `StageRuns`. Each `StageRunRecord` needs a `ModelAlias` field added (nullable, opt-in — existing runs without it gracefully produce null entries).

**Least-invasive hook** (preferred over full StageRunRecord schema change):

Option A — extend `StageRunRecord` with optional `ModelAlias`: cleanest; one migration.
Option B — `StageRunDetail` lookup table keyed by `StageRunId` → `ModelAlias`: no migration; new table.

Recommend **Option A** — `StageRunRecord` already stores `ExecutionProvider`, `ModelAlias` is the natural companion. Add it as nullable string; existing SQLite rows read null → manifest shows "unknown".

---

## 7. Export manifest builder changes

`ExportManifestBuilder.Build()` gains:

```csharp
IReadOnlyList<ExportManifestModel> contributingModels = BuildContributingModels(request, catalog);
IReadOnlyList<ExportAttributionRequirement> attributionRequired =
    contributingModels
        .Where(m => m.RequiresAttribution)
        .Select(m => BuildAttributionRequirement(m, catalog))
        .ToArray();

return new ExportManifest(
    // ... existing fields ...
    ContributingModels: contributingModels,
    AttributionRequired: attributionRequired);
```

`BuildContributingModels`:
1. Collect TTS model IDs from `TtsTakes.ModelId` (existing) — stage = "tts"
2. Collect per-stage model aliases from `request.StageModels` — stages asr/translation/separation/diarization
3. Deduplicate by `(Stage, ModelAlias)`.
4. For each: lookup `ModelAttributionCatalog.Find(alias)` for `IsCloud`, `License`, `RequiresAttribution`.

---

## 8. Export success UI — attribution surface

After export completes, the `ExportMixView`/`ExportMixViewModel` shows:

```
✓  Export complete  →  dubbed.mp4  [Show in folder]

Models used:
  ASR        whisper-tiny-genai    (local, no attribution required)
  Diarization sortformer-4spk      (local) ★ attribution required
  Translation deepl-cloud           (DeepL cloud service)
  TTS        kokoro-onnx            (local) ★ attribution required

Attribution required for this export:
  • sortformer by NVIDIA — NVIDIA-Open-Model-License
    https://huggingface.co/cgus/diar_streaming_sortformer_4spk-v2.1-onnx
  • Kokoro-82M by Hexgrad — Apache-2.0
    https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX

Attribution details saved to: dubbed.export-manifest.json
```

If no attribution required and no cloud: show nothing extra (don't add noise for the common local all-no-attribution case).

`ExportMixViewModel` binds to:
```csharp
IReadOnlyList<ExportManifestModel> ContributingModels { get; }
IReadOnlyList<ExportAttributionRequirement> AttributionRequired { get; }
bool HasAttributionRequired => AttributionRequired.Count > 0;
bool HasCloudProviders { get; }
```

---

## 9. Components by layer

| Layer | Change |
|---|---|
| `Trackdub.Contracts` | Extend `StageRunRecord` with `string? ModelAlias` (nullable, migration-safe). |
| `Trackdub.Application` | `ModelAttributionEntry` record; `IModelAttributionCatalog` + `ModelAttributionCatalog` static impl; `ExportManifestModel` + `ExportAttributionRequirement` records; extend `ExportManifest` + `ExportManifestBuildRequest` + `ExportManifestBuilder`; extend `StageRunHelper.CompleteAsync` to accept optional `modelAlias`. |
| `Trackdub.Infrastructure` | SQLite migration: add `ModelAlias` column to stage runs table (nullable, no-op on existing rows). |
| `Trackdub.Composition` | Register `IModelAttributionCatalog` populated from `bundled-models.manifest.json` at startup; inject into `ExportManifestBuilder`. Extend each stage handler's `CompleteAsync` call to pass `executionSummary.ModelAlias`. |
| `Trackdub.App.Avalonia` | `ExportMixViewModel`: bind `HasAttributionRequired`, `ContributingModels`, `AttributionRequired`; show attribution section in `ExportMixView.axaml` when `HasAttributionRequired`. |

No inference code in App; no SQL in VMs; layer boundaries preserved.

---

## 10. Build sequence

1. **Contracts** — extend `StageRunRecord` with `ModelAlias?`.
2. **Application** — `ModelAttributionEntry`, `IModelAttributionCatalog`, `ExportManifestModel`, `ExportAttributionRequirement`; extend `ExportManifest` + builder.
3. **Infrastructure** — SQLite migration + extend stage run persistence to write `ModelAlias`.
4. **Composition** — catalog registration; each stage handler passes `ModelAlias` on complete.
5. **App** — `ExportMixViewModel` fields; `ExportMixView` attribution section.

---

## 11. Tests

- `ExportManifestBuilder`: empty `StageModels` → `ContributingModels` has TTS models only (backward compat).
- With `StageModels`: all stages reflected; deduplication works.
- `AttributionRequired` contains only entries where `RequiresAttribution=true`.
- Cloud alias (e.g. `"deepl-cloud"`) → `IsCloud=true`, `RequiresAttribution=false`, `CloudProviderKey="deepl"`.
- `ModelAttributionCatalog.Find("kokoro-onnx")` → `RequiresAttribution=true`, `License="Apache-2.0"`.
- `ModelAttributionCatalog.Find("whisper-tiny-onnx")` → `RequiresAttribution=false`.
- Export with no attribution-required models → `AttributionRequired` empty; UI attribution section hidden.
- SQLite migration: existing stage run rows with null `ModelAlias` → manifest `ContributingModels` omits those entries (no crash).

---

## 12. Open questions

- **StageRunHelper.CompleteAsync signature:** currently takes `runtimeReporter` (the reporter instance). Extract `ModelAlias` from it there, or pass as explicit param? Extracting from reporter is cleaner — no new param to existing callers.
- **Opus-MT attribution text:** CC-BY-4.0 requires attribution, but the 13 opus-mt pairs each have their own HF model page. Show one entry per model used or one "opus-mt family" entry? One-per-model is more precise; merge if user feedback shows it's overwhelming.
- **Cloud service terms:** DeepL/OpenAI TTS/ElevenLabs terms of service require their own acknowledgments beyond what G3 consent covers. Not specced here — flag for legal review.
