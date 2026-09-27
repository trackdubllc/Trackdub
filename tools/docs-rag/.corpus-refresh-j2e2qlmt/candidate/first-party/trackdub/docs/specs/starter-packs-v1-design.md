# Starter packs v1 design

**Status:** Implementation-ready (approved 2026-06-12).  
**Source of truth:** `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`  
**Consumers:** `Trackdub.Cli` (headless + TUI), `Trackdub.App.Avalonia` (Model Manager), `Trackdub.Sdk` (readiness).

## Goal

Ship three starter packs that make first-run model setup understandable and hardware-aware without creating a second model-selection system:

1. **Basic / Fast** — smallest usable dubbing spine.
2. **Balanced / Mid** — creator-ready spine with diarization and better ASR.
3. **Premium / Quality** — higher-quality ASR, voice cloning, and dedicated multilingual translation.

Starter packs must:

- List bundled commercial models to download for a usable dubbing spine.
- Declare runtime defaults per hardware profile: manifest **alias**, **variant alias**, and **execution provider**.
- Document optional Olive targets as recommendations only.
- Integrate with existing hardware profiler, `IRuntimePlanningPreferences`, `RuntimePlannerRankingStrategy`, EP smoke tests, and `RuntimePlanFallback`.

## Non-goals v1

- Do not auto-run Olive during pack download.
- Do not include non-commercial, experimental lane, or blocked models in starter-pack download sets.
- Do not include lip sync.
- Do not replace Model Manager stage grouping in Avalonia (tier bands are additive).
- Do not promise a language pair default such as English → Spanish.
- Do not co-download alternate ASR models with the primary ASR model.
- Do not use `Xenova/whisper-small` in pack authoring (not in bundled manifest for shipping ONNX path).

---

## Locked product decisions

| Area | Decision |
|---|---|
| Pack location | Bundled defaults ship beside the executable. User overrides: `{InstallDir}/StarterPacks/*.json`. `InstallDir` = `AppContext.BaseDirectory` (portable and installed builds). Not repo root. Not AppData. |
| Workflow | `download` = models only. `apply` = aliases, variants, EP defaults, tier preference. `optimize` = explicit post-download Olive. |
| Windows copy | Say **GPU-optimized**, not CUDA. Runtime uses TRT RTX EP ABI plugin (`trt-rtx`) or Windows ML/DirectML fallback (`directml`). |
| Linux copy | Say CUDA / TensorRT when smoke-tested and available. |
| Translation | Universal pivot models in packs. Opus-mt pair models are optional add-ons only. |
| Diarization | Balanced-required. Optional in Basic. |
| Balanced ASR | Primary profile: `openai/whisper-small`. Alternate profile `balanced-multilingual`: `tonythethompson/qwen3-asr-0.6b-onnx`. |
| Premium ASR | Primary profile: `openai/whisper-medium`. Alternate profile `premium-multilingual`: `tonythethompson/qwen3-asr-1.7b-onnx`. |
| Voice cloning | Premium with consent. Chatterbox family has `requires_user_consent: true`. |
| Lip sync | Excluded v1. `musetalk-v1-5` is blocked. |
| CPU-only fallback | Use Basic-fast behavior. `CpuSafe` must map to manifest tier `fast`. |

---

## Three-phase workflow

```text
models packs download <id> [--profile <profile-id>] [--yes]
models packs apply <id>   [--profile <profile-id>] [--hardware-profile <profile>] [--yes]
models optimize           [--pack <id>] [--yes]    # existing optimize entry; pack-scoped optional
```

Rules:

- `download` must not change aliases or run Olive.
- `apply` must validate model IDs, variants, EPs, and commercial policy before persisting settings.
- `optimize` is user-triggered; failure keeps the base model and reports fallback honestly.
- Premium `apply` with Chatterbox must require `--accept-voice-cloning-consent` or an interactive consent prompt (TUI/desktop).

---

## Tier mapping

| Starter pack | Pack `tier_preference` | Manifest `tier` values used for planner bias | Typical hardware profile |
|---|---|---|---|
| Basic / Fast | `fast` | `fast` | `cpu_safe` |
| Balanced / Mid | `balanced` | `balanced` | `balanced_gpu` |
| Premium / Quality | `quality` | `quality`, `accurate` | `turbo_gpu` |

Profiler quirk (existing): `HardwareQualityPreset.Turbo` maps to manifest tier string `"fast"` in `HardwarePresetRecommendation.ToModelTierPreference`. Do not rename in v1; starter packs set `tier_preference` explicitly on apply.

**Required fix before packs ship:** `CpuSafe` must map to `"fast"`, not `"balanced"` (`src/Trackdub.Domain/HardwareProfiler.cs` line ~108).

---

## Hardware profiles

| Profile | Detected when | Variant bias | EP bias (Windows) | EP bias (Linux) |
|---|---|---|---|---|
| `cpu_safe` | No GPU or profiler `CpuSafe` | int4/int8/q4 | `cpu` | `cpu` |
| `balanced_gpu` | GPU present, moderate VRAM | fp16 / gpu-int4 | `directml` | `cuda` when smoke-tested |
| `turbo_gpu` | High-end GPU + TRT RTX plugin or catalog EP smoke pass | fp16 / gpu-int4 | `trt-rtx` then `directml` | `tensorrt` / `cuda` when smoke-tested |

When `--hardware-profile` is omitted on `apply`, resolve from latest `IHardwareProfilerService` recommendation:

| `HardwareQualityPreset` | Pack hardware profile |
|---|---|
| `CpuSafe` | `cpu_safe` |
| `Balanced` | `balanced_gpu` |
| `Turbo`, `Quality` | `turbo_gpu` |

---

## Starter pack download sets

### Basic / Fast (`id: basic`)

| Model ID | Stage | Required | Manifest alias (apply) |
|---|---|---|---|
| `onnx-community/silero-vad` | `vad` | yes | `silero-vad` |
| `onnx-community/whisper-tiny` | `asr` | yes | `whisper-tiny` |
| `onnx-community/Kokoro-82M-v1.0-ONNX` | `tts` | yes | `kokoro-onnx` |
| `microsoft/Phi-4-mini-instruct-onnx` | `translation` | yes | `phi-4-mini` |

Optional: `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` (`sortformer-4spk`), `csukuangfj/sherpa-onnx-spleeter-2stems`.

### Balanced / Mid (`id: balanced`)

| Model ID | Stage | Required | Manifest alias (apply) |
|---|---|---|---|
| `onnx-community/silero-vad` | `vad` | yes | `silero-vad` |
| `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` | `diarization` | yes | `sortformer-4spk` |
| `openai/whisper-small` | `asr` | yes (profile `default`) | `whisper-small-genai` |
| `tonythethompson/qwen3-asr-0.6b-onnx` | `asr` | yes (profile `balanced-multilingual`) | `qwen3-asr-0.6b` |
| `onnx-community/Kokoro-82M-v1.0-ONNX` | `tts` | yes | `kokoro-onnx` |
| `microsoft/Phi-4-mini-instruct-onnx` | `translation` | yes | `phi-4-mini` |

Optional: `csukuangfj/sherpa-onnx-spleeter-2stems`, `Rikorose/DeepFilterNet3`.

### Premium / Quality (`id: premium`)

| Model ID | Stage | Required | Manifest alias (apply) |
|---|---|---|---|
| `onnx-community/silero-vad` | `vad` | yes | `silero-vad` |
| `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` | `diarization` | yes | `sortformer-4spk` |
| `openai/whisper-medium` | `asr` | yes (profile `default`) | `whisper-medium-genai` |
| `tonythethompson/qwen3-asr-1.7b-onnx` | `asr` | yes (profile `premium-multilingual`) | `qwen3-asr-1.7b` |
| `ResembleAI/chatterbox-turbo-ONNX` | `tts` | yes | `chatterbox-turbo-onnx` |
| `google/madlad400-3b-mt` | `translation` | yes | `madlad400` |

Optional: Qwen2.5 polish, Nemotron, Phi translation upgrades, spleeter, opus-mt pairs, alternate chatterbox clones.

**Honesty gate:** `sortformer` has `commercial_use_verified: false` in manifest today. Packs may list it; readiness UI must not claim commercial verification until manifest is updated.

---

## Translation strategy

Packs use **universal** pivot models. No `translation_pair` in pack defaults.

| Pack | Model | Alias on apply |
|---|---|---|
| Basic, Balanced | `microsoft/Phi-4-mini-instruct-onnx` | `phi-4-mini` |
| Premium | `google/madlad400-3b-mt` | `madlad400` |

`TranslationLanguageRouter` resolution order (when override is `Auto` and no explicit stage alias):

1. User-preferred alias (from `StageModelAliases` or pipeline selection).
2. Direct Opus route if that pair model is installed.
3. Madlad pivot.
4. Phi genai pivot.

Opus-mt models remain optional manifest entries for advanced `models packs add --pair en-es`. Not in starter download sets.

---

## Pack JSON schema

Files: `basic.json`, `balanced.json`, `premium.json` (bundled) plus optional user files in `{InstallDir}/StarterPacks/`.

```jsonc
{
  "schema_version": 1,
  "id": "basic",                        // stable slug
  "display_name": "Basic / Fast",
  "tier_preference": "fast",            // persisted to StudioSettings.ModelTierPreference
  "description": "Smallest dubbing spine: VAD, tiny ASR, Kokoro TTS, universal Phi translation.",
  "profiles": [                         // omit or single "default" for basic
    {
      "id": "default",
      "display_name": "Default",
      "asr_model_id": "onnx-community/whisper-tiny"   // only field that differs between profiles
    }
  ],
  "models": [
    {
      "model_id": "onnx-community/silero-vad",
      "stage": "vad",
      "required": true,
      "alias": "silero-vad",
      "runtime_defaults": {
        "cpu_safe":     { "variant": "int8",  "execution_provider": "cpu" },
        "balanced_gpu": { "variant": "fp16",  "execution_provider": "directml" },
        "turbo_gpu":    { "variant": "fp16",  "execution_provider": "trt-rtx" }
      },
      "olive": null
    }
  ],
  "translation": {
    "strategy": "universal",              // "pair" only for advanced add-on profiles
    "model_id": "microsoft/Phi-4-mini-instruct-onnx",
    "alias": "phi-4-mini"
  },
  "optional_models": [ "cgus/diar_streaming_sortformer_4spk-v2.1-onnx" ],
  "olive_auto_run": false
}
```

Validation (`StarterPackValidator`):

- `schema_version` must be `1`.
- Every `model_id` exists in bundled manifest with `commercial_allowed: true` and no `lane: experimental`.
- Every `variant` exists on that manifest entry's `variants[]` (or `"default"` when the model has no variants).
- Every `execution_provider` is a known `ExecutionProviderKind` string (`cpu`, `directml`, `cuda`, `trt-rtx`, `tensorrt`, `migraphx`).
- Exactly one primary ASR per profile (profile picks `asr_model_id`; other stages shared).
- `translation.strategy` is `universal` for shipping packs unless `id` ends with `-pair-addon` (advanced user pack convention).
- User pack with same `id` as bundled pack overrides bundled (load order: bundled first, user dir wins on duplicate `id`).

---

## Prerequisite code changes (blockers)

These are required before `apply` can work end-to-end in CLI and desktop:

### 1. Persist per-stage model aliases

`StudioSettings` today only has coarse overrides (`AsrModelOverride`, `TranslationModelOverride`, …). GenAi override resolves to `whisper-tiny-genai`, not `whisper-small-genai`.

**Add to `StudioSettings`:**

```csharp
IReadOnlyDictionary<string, string>? StageModelAliases = null  // keys: StageNames.* (asr, translation, tts, diarization, vad)
string? AppliedStarterPackId = null
string? AppliedStarterPackProfileId = null
```

**Wire consumers:**

- `RuntimeModelRequestFactory.CreateSelectionsFromSettings` — resolves aliases with precedence: explicit `InferenceModelPreferences` > `StageModelAliases` > override enums > defaults. Used by SDK dubbing pre-flight and default pipeline readiness.
- `AvaloniaMainWindowViewModel.PipelineUi` `CreateRuntimeSelections` — seed `GetSelectedModelAlias` from `StageModelAliases` when pipeline row has no selection (Avalonia follow-up).
- `PipelineStageModelCatalog.ResolveInitialSelection` — prefer `StageModelAliases[stageKey]` before first inventory option (Avalonia follow-up).

### 2. CpuSafe → fast

`HardwarePresetRecommendation.ToModelTierPreference`: change `CpuSafe => "fast"`.

### 3. Translation override for Premium

Premium apply sets `TranslationModelOverride.Madlad` (maps to `madlad400`). Basic/Balanced set `TranslationModelOverride.Auto` **and** `StageModelAliases["translation"] = "phi-4-mini"` so router does not depend on download order.

---

## Pack apply contract

`StarterPackApplyService.ApplyAsync(pack, profileId, hardwareProfile)` mutates `StudioSettings` as follows:

| Field | Basic | Balanced (`default`) | Balanced (`balanced-multilingual`) | Premium (`default`) |
|---|---|---|---|---|
| `ModelTierPreference` | `fast` | `balanced` | `balanced` | `quality` |
| `AppliedStarterPackId` | `basic` | `balanced` | `balanced` | `premium` |
| `AppliedStarterPackProfileId` | `default` | `default` | `balanced-multilingual` | `default` |
| `AsrModelOverride` | `OnnxRuntime` | `GenAi` | `Auto` | `GenAi` |
| `TranslationModelOverride` | `Auto` | `Auto` | `Auto` | `Madlad` |
| `TtsModelOverride` | `Kokoro` | `Kokoro` | `Kokoro` | `Chatterbox` |
| `StageModelAliases["asr"]` | `whisper-tiny` | `whisper-small-genai` | `qwen3-asr-0.6b` | `whisper-medium-genai` |
| `StageModelAliases["translation"]` | `phi-4-mini` | `phi-4-mini` | `phi-4-mini` | `madlad400` |
| `StageModelAliases["tts"]` | `kokoro-onnx` | `kokoro-onnx` | `kokoro-onnx` | `chatterbox-turbo-onnx` |
| `StageModelAliases["diarization"]` | (omit) | `sortformer-4spk` | `sortformer-4spk` | `sortformer-4spk` |

**Variant overrides** (`ModelVariantOverrideKeys.Build(stage, alias)`):

| Stage | Alias | `cpu_safe` | `balanced_gpu` / `turbo_gpu` |
|---|---|---|---|
| `vad` | `silero-vad` | `int8` | `fp16` |
| `asr` | per profile | see runtime table below | see runtime table below |
| `translation` | `phi-4-mini` | `cpu-int4` | `gpu-int4` |
| `translation` | `madlad400` | `quantized` | `default` |
| `tts` | `kokoro-onnx` | `default` | `default` |
| `tts` | `chatterbox-turbo-onnx` | `q4` | `fp16` |

**Hardware overrides** (`HardwareOverrides` keys from `HardwareOverrideCatalog`):

| Key | `cpu_safe` | `balanced_gpu` | `turbo_gpu` |
|---|---|---|---|
| `Vad` | `Cpu` | `DirectMl` / `Cuda` | `TensorRTRtx` / `Cuda` |
| `AsrGenAi` | `Cpu` | `DirectMl` / `Cuda` | `TensorRTRtx` / `Cuda` |
| `AsrOnnxRuntime` | `Cpu` | `DirectMl` | `DirectMl` |
| `Translation` | `Cpu` | `DirectMl` / `Cuda` | `TensorRTRtx` / `Cuda` |
| `Tts` | `Cpu` | `DirectMl` | `TensorRTRtx` / `DirectMl` |
| `Diarization` | `Cpu` | `DirectMl` / `Cuda` | `Cuda` |

Use `null` provider (= Auto) when pack JSON says `"execution_provider": "auto"` for a stage; omit key instead of writing Auto explicitly.

---

## Services and file layout

| Artifact | Project | Path |
|---|---|---|
| Pack JSON (bundled) | `Trackdub.Composition` | `StarterPacks/basic.json`, `balanced.json`, `premium.json` |
| Copy to output | `Trackdub.Composition.csproj` | `<Content Include="StarterPacks\*.json" CopyToOutputDirectory="PreserveNewest" />` |
| `StarterPackDefinition` record | `Trackdub.Contracts` | `StarterPacks/StarterPackDefinition.cs` |
| `IStarterPackCatalog` | `Trackdub.Contracts` | `StarterPacks/IStarterPackCatalog.cs` |
| `StarterPackCatalog` | `Trackdub.Composition` | `StarterPacks/StarterPackCatalog.cs` |
| `StarterPackValidator` | `Trackdub.Composition` | `StarterPacks/StarterPackValidator.cs` |
| `StarterPackDownloadService` | `Trackdub.Composition` | `StarterPacks/StarterPackDownloadService.cs` |
| `StarterPackApplyService` | `Trackdub.Composition` | `StarterPacks/StarterPackApplyService.cs` |
| CLI commands | `Trackdub.Cli` | extend `Commands/ModelsCommand.cs` |
| CLI handlers | `Trackdub.Cli` | `Handlers/StarterPacksHandler.cs` |
| TUI | `Trackdub.Cli` | extend `Tui/Screens/ModelsTuiScreen.cs` |
| DI registration | `Trackdub.Composition` | `CompositionRoot.cs` |

### `IStarterPackCatalog`

```csharp
Task<IReadOnlyList<StarterPackSummary>> ListAsync(CancellationToken ct);
Task<StarterPackDefinition> GetAsync(string packId, CancellationToken ct);
string UserPacksDirectory { get; }  // Path.Combine(AppContext.BaseDirectory, "StarterPacks")
```

Load order: parse bundled `StarterPacks/*.json` from `AppContext.BaseDirectory`, then overlay `{UserPacksDirectory}/*.json` (user wins on duplicate `id`).

### `StarterPackDownloadService`

- Input: pack id, profile id, optional progress `IProgress<ModelDownloadProgress>`.
- Resolve required `model_id` list = all `models[].required == true` plus profile's `asr_model_id` if not already listed.
- For each id: `IModelDownloadOrchestrator.DownloadAsync` (same as `models download`).
- Do not call Olive or `IStudioSettingsService.SaveAsync`.
- Return `StarterPackDownloadResult` with per-model success/failure.

### `StarterPackApplyService`

- Input: pack id, profile id, hardware profile (or auto-detect).
- Validate all required models are `Ready` or `Installed` in `IModelInventoryService` (warn, do not block apply — user may apply before download completes; document that pipeline will skip until ready).
- Build `StudioSettings` patch per **Pack apply contract** above; `IStudioSettingsService.SaveAsync`.
- Return `StarterPackApplyResult` listing what changed.

---

## CLI command surface

Register under `trackdub models packs` in `ModelsCommand.Create()`.

```text
trackdub models packs list [--json]
trackdub models packs show <pack-id> [--profile <id>] [--json]
trackdub models packs download <pack-id> [--profile <id>] [--yes]
trackdub models packs apply <pack-id> [--profile <id>] [--hardware-profile cpu_safe|balanced_gpu|turbo_gpu] [--accept-voice-cloning-consent] [--yes]
trackdub models packs add <model-id> [--yes]          # optional model; no settings change
trackdub models packs add-pair <source>-<target> [--yes]  # resolves opus manifest entry; optional
```

**`list --json` shape:**

```json
{
  "packs": [
    {
      "id": "balanced",
      "display_name": "Balanced / Mid",
      "tier_preference": "balanced",
      "profiles": ["default", "balanced-multilingual"],
      "required_model_ids": ["onnx-community/silero-vad", "..."],
      "optional_model_ids": ["..."],
      "applied": false
    }
  ],
  "recommended_pack_id": "balanced",
  "hardware_profile": "balanced_gpu"
}
```

`recommended_pack_id` from `IHardwareProfilerService` + simple rules (no GPU → `basic`, etc.).

Exit codes: reuse `Program.ExitSuccess`, `ExitPipelineFailure`, `ExitCancelled` from existing CLI.

---

## TUI implementation (`ModelsTuiScreen`)

Extend `src/Trackdub.Cli/Tui/Screens/ModelsTuiScreen.cs`. Keep existing flat inventory table; add pack mode via overlay picker (same pattern as download menu).

### Footer (no overlay)

```text
Models actions:  p  packs menu   d  download menu   a  all missing   v  verify
```

### Packs menu (`p`)

Picker choices:

1. **List packs** — table: Pack | Tier | Profile | Required | Ready | Applied
2. **Download pack…** — pick pack → pick profile (if >1) → confirm → sequential download with existing progress reporting
3. **Apply pack…** — pick pack → profile → confirm hardware profile (default from profiler) → apply → status message
4. **Add optional model…** — pick from current pack's `optional_models` or full missing list
5. Back

### Display conventions

- Use `TuiMarkup.FormatModelLabel` for model ids.
- GPU copy: "GPU-optimized (DirectML)" on Windows; "CUDA" on Linux only when `OperatingSystem.IsLinux()`.
- After apply, set `context.StatusMessage` with pack id + profile + hardware profile.
- Premium apply: if pack includes `chatterbox-turbo-onnx` and consent not yet recorded, show consent picker before apply (mirror Avalonia consent flags in `StudioSettings` if they exist, or set a new `VoiceCloningConsentAccepted` bool).

### v1 TUI truth model (implemented 2026-06-12)

Starter packs are an **onboarding layer** over the existing manifest/runtime planner. CLI and Avalonia call **`IStarterPackCoordinator`** (Composition) only; they do not parse pack JSON or compute planner truth. `StarterPacksHandler` is a thin Cli adapter over the coordinator.

### Add a bundled pack once

1. Add JSON under `src/Trackdub.Composition/StarterPacks/` (and csproj copy rule if needed).
2. Extend `StarterPackApplyContract` when new override mapping is required.
3. Add/adjust validator tests in `Trackdub.Composition.Tests`.
4. **Stop.** CLI TUI and Avalonia pick up the pack via `ListAsync()` — do not duplicate pack logic in shell projects.

**Apply coverage (architecture tests):** bundled shipping packs must have an apply contract entry or explicit `pack_kind: cloud` branch (cloud in a later PR). User packs (future) validate through data-driven `apply` JSON, not `StarterPackApplyContract`.

**Pack panel columns:** Pack | Profile | Required | Installed | Status

| Term | Meaning |
|---|---|
| **Installed** | Required model files present with cache state `Ready` or `Installed` and checksum-valid inventory |
| **Runtime-ready** | Planner + EP smoke say the selected variant/EP is runnable (display-only in v1.1; never gates Apply in v1) |

**Status values (pack-level):**

- `applied` — `AppliedStarterPackId` matches
- `recommended` — hardware profiler suggestion (`CpuSafe` → `fast` → Basic pack)
- `license review needed` — any **required** model has `commercial_use_verified: false` in manifest

**Apply gates (all must pass; transactional, all-or-nothing):**

1. `Installed == Required` for the pack/profile (checksum-valid; not smoke-verified).
2. Every **required** model has `commercial_use_verified: true`. Block with `License review needed: {alias} is not commercial-use verified.`
3. Consent metadata satisfied for models with `requires_user_consent` / voice-cloning flags.

**Download vs Apply:** Download never mutates `StudioSettings`. Apply persists aliases, variants, EP keys, tier, and applied-pack IDs in one write.

**v1 apply-ready reality:** Until manifest verification catches up (today: `sortformer-4spk` on Balanced/Premium; `phi-4-mini` may also block Basic), only packs with all required models `commercial_use_verified: true` can Apply. Balanced/Premium remain downloadable but Apply-blocked.

**Footer:**

```text
p packs   d ad-hoc download   a all missing   v verify
```

**`p` menu (two separate actions, no combined shortcut):**

1. Download pack… — files only; works when license review is pending.
2. Apply pack… — blocked until installed + commercial-verified + consent OK.

**Key schemes (do not conflate):**

| Dictionary | Key source | Examples |
|---|---|---|
| `StageModelAliases` | `StageNames` | `asr`, `translation`, `tts`, `diarization` |
| `HardwareOverrides` | `HardwareOverrideCatalog` | `AsrGenAi`, `AsrOnnxRuntime`, `Separation`, `Diarization` |

Apply **merges** pack-owned keys into existing `HardwareOverrides` and `ModelVariantOverrides`; unrelated user overrides are preserved.

**Override precedence:**

```text
Explicit session/project override
  > StageModelAliases from applied pack
  > existing model override enum (Asr/Translation/Tts)
  > built-in default / planner ranking
```

Implemented in `RuntimeModelRequestFactory.CreateSelectionsFromSettings`; SDK dubbing/readiness paths load `StudioSettings` and call this helper.

### Tier-band view (v1 stretch inside TUI)

Optional sub-mode `t` toggles grouping:

- **Pack: Basic** — manifest models whose `tier` is `fast` and appear in `basic.json`
- **Pack: Balanced** — `balanced` tier + balanced pack list
- **Pack: Premium** — `quality`/`accurate` + premium pack list
- **Optional add-ons** — union of all `optional_models` across packs + opus-mt entries
- **All** — current flat list (default)

Implementation: filter `ModelInventoryEntry` by manifest `tier` and pack membership; no new inventory API required.

---

## Avalonia implementation (`ModelManagerViewModel`)

v1 scope (no first-run wizard gate — that is v1.1):

1. **Tier band filter** dropdown: All | Basic pack | Balanced pack | Premium pack | Optional add-ons.
2. **Applied pack badge** in Model Manager header when `AppliedStarterPackId` is set (read `IStudioSettingsService`).
3. **Actions** (mirror CLI): Download pack, Apply pack — call shared `StarterPackDownloadService` / `StarterPackApplyService` from Composition (inject into `ModelManagerViewModel`).
4. **Consent** — reuse existing chatterbox consent UI before Premium apply.

First-run chooser (v1.1): when `ShowLocalModelsAtStartup` and no models ready and no `AppliedStarterPackId`, show pack recommendation dialog.

---

## Fallback and degradation

If planner/smoke rejects an EP:

- Substitute next EP in pack order for that hardware profile (TRT-RTX → DirectML → CPU on Windows).
- If model tier fails VRAM gate, log degradation; do not auto-download a different pack.
- User-visible message template:

```text
GPU path unavailable for {alias} ({model_id}). Using {variant} on {execution_provider}.
```

Structured downgrade ladder (separate task): persist fallback reason in `PipelineDegradationRecord` when apply-time EP substitution occurs.

---

## Tests

| Layer | Project | Cases |
|---|---|---|
| Validator | `Trackdub.Composition.Tests` or `Trackdub.Application.Tests` | Rejects unknown model_id, experimental lane, bad variant, invented olive path |
| Catalog | same | Bundled load, user override wins, missing file graceful |
| Apply | `Trackdub.Application.Tests` | Basic apply sets `StageModelAliases`, tier, variant keys; Premium sets Madlad override + chatterbox |
| CpuSafe | `Trackdub.Domain.Tests` | `ToModelTierPreference(CpuSafe) == "fast"` |
| CLI | `Trackdub.Cli.Tests` or `Trackdub.Sdk.Tests` | `models packs list --json` smoke; download/apply integration with fakes |
| Readiness | `Trackdub.Sdk.Tests` | After apply+bundle download fakes, `bundle-needed` reflects spine |

---

## Implementation order

| Step | Owner | Deliverable |
|---|---|---|
| 1 | Composition | Pack JSON files + `StarterPackDefinition` + validator |
| 2 | Contracts + Composition | `IStarterPackCatalog`, loader, DI |
| 3 | Contracts + Infrastructure | `StageModelAliases`, `AppliedStarterPack*` on `StudioSettings` + JSON persistence |
| 4 | Application | Wire `StageModelAliases` in `RuntimeModelRequestFactory` + Avalonia pipeline seed |
| 5 | Domain | CpuSafe → `fast` |
| 6 | Composition | Download + Apply services |
| 7 | Cli | `models packs *` commands + `StarterPacksHandler` |
| 8 | Cli TUI | Packs overlay on `ModelsTuiScreen` |
| 9 | App.Avalonia | Model Manager tier filter + apply/download buttons |
| 10 | Later | First-run wizard, structured downgrade ladder, `models packs optimize` pack scope |

---

## Acceptance checks

- [ ] Basic required downloads: `silero-vad`, `whisper-tiny`, `Kokoro`, `Phi-4-mini` (by model_id).
- [ ] Balanced required: above spine + `sortformer` + `whisper-small` (default profile) + `Phi-4-mini`.
- [ ] Premium required: `sortformer`, `whisper-medium`, `chatterbox-turbo`, `madlad400`, `silero-vad`.
- [ ] No Opus pair in any pack `models[].required`.
- [ ] No co-download of both `whisper-small` and `qwen3-asr-0.6b` for same profile apply.
- [ ] `download` never runs Olive or changes `settings.json`.
- [ ] `apply` persists `ModelTierPreference`, `StageModelAliases`, overrides, variant keys, hardware keys, `AppliedStarterPackId`.
- [ ] CLI `models packs list --json` and TUI packs menu work offline against bundled JSON only.
- [ ] `CpuSafe` → `fast`.
- [ ] Premium apply blocked without voice cloning consent.
- [ ] Windows UI strings say GPU-optimized, not CUDA.

---

## References

| Topic | Path |
|---|---|
| Manifest | `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json` |
| Tier bias | `src/Trackdub.Inference/Runtime/Planning/RuntimePlannerRankingStrategy.cs` |
| Hardware profiler | `src/Trackdub.Domain/HardwareProfiler.cs` |
| Hardware overrides | `src/Trackdub.Application/Runtime/HardwareOverrideCatalog.cs` |
| Variant keys | `src/Trackdub.Contracts/IStudioSettingsService.cs` (`ModelVariantOverrideKeys`) |
| Runtime selections | `src/Trackdub.Application/Transcripts/RuntimeModelRequestFactory.cs` |
| Translation router | `src/Trackdub.Inference.Onnx/Translation/TranslationLanguageRouter.cs` |
| Model download | `src/Trackdub.Contracts/IModelDownloadOrchestrator.cs` |
| Models CLI | `src/Trackdub.Cli/Commands/ModelsCommand.cs`, `Handlers/ModelsHandler.cs` |
| Models TUI | `src/Trackdub.Cli/Tui/Screens/ModelsTuiScreen.cs` |
| Storage paths | `src/Trackdub.Infrastructure/Settings/TrackdubStoragePaths.cs` |
| Pipeline readiness | `src/Trackdub.Sdk/TrackdubPipelineReadinessChecker.cs` |
| Windows EP ADR | `docs/adr/ADR-0002-windows-ml-provider-strategy.md` |
| CLI/TUI design | `docs/superpowers/specs/2026-06-06-trackdub-cli-tui-design.md` |
