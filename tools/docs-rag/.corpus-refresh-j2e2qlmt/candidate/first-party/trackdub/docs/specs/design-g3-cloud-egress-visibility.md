# Design Spec — G3: Cloud Egress Visibility & Consent

**Source gap:** [service-blueprint-first-dub.md](service-blueprint-first-dub.md) · Gap **G3** — local-vs-cloud engine choice is invisible before Run; `CloudAware*` wrappers route silently by `PreferredModelAlias`; data can egress to external providers without user knowledge.

**Relationship to G5:** G3 builds on the readiness panel and `ReadinessState` enum introduced in [design-g5-readiness-gate.md](design-g5-readiness-gate.md). G5's `CloudKeyMissing` state becomes one entry point into G3's consent flow; `CloudEgressConsentRequired` is the new state G3 adds.

**Scope discipline:** this spec covers *disclosure* and *consent* for cloud data egress. Key management (storage, validation) stays in `ApiKeyStore` / `EnvironmentCloudApiKeyProvider`. Model-tier selection UI is out of scope — G3 is about what happens *after* a cloud engine is chosen.

---

## 1. Problem — egress is invisible, consent is absent

**What leaves the machine (confirmed from source):**

*Per-stage pipeline (stages run individually through `TrackdubDubbingEngine.DefaultStageOrder`):*

| Stage | Provider | Data sent | Sensitivity |
|---|---|---|---|
| ASR | OpenAI (`openai-whisper-cloud`) | `File.ReadAllBytes(normalizedAudioPath)` — **entire normalized audio file** (WAV/MP3, raw speech) | **High** — raw audio |
| ASR | Gemini (`gemini-asr-cloud`) | Same — full audio file bytes | **High** |
| Translation | DeepL (`deepl-cloud`) | `segment.Text[]` — transcript text strings | **Med** |
| Translation | OpenAI GPT (`openai-gpt-cloud`) | Same — segment text | **Med** |
| Translation | Gemini (`gemini-translation-cloud`) | Same — segment text | **Med** |
| TTS | ElevenLabs (`elevenlabs-tts-cloud`) | `request.Text` per segment + `voiceId` | **Low-Med** — derived text |
| TTS | OpenAI TTS (`openai-tts-cloud`) | Same — text per segment | **Low-Med** |
| TTS | Google TTS (`google-tts-cloud`) | Same — text per segment | **Low-Med** |

*Cloud dubbing lane (bypasses all stages — `ICloudDubbingEngine`):*

| Lane | Provider | Data sent | Sensitivity |
|---|---|---|---|
| Cloud dub (full pipeline) | ElevenLabs (`ElevenLabsCloudDubbingEngine`) | `File.ReadAllBytes(MediaFilePath)` — **entire source media file** (video+audio, MP4/MKV/WAV) | **Critical** — full video with all audio content |

> **Architecture note:** The cloud dubbing lane is a completely separate path from the per-stage pipeline. `ElevenLabsCloudDubbingEngine.DubAsync` submits the raw media file to `api.elevenlabs.io/v1/dubbing`, polls for completion (up to 30 min), and returns `byte[] AudioBytes`. ElevenLabs handles ASR + translation + TTS internally — Trackdub receives only the final dubbed audio. No transcript, no speaker labels, no segment-level editing.
>
> **Current wiring status:** `ElevenLabsCloudDubbingEngine` is registered in `CompositionRoot` and fully implemented, but **`ICloudDubbingEngine.DubAsync` has no callsite** in the app or SDK as of this writing. The lane is scaffolded, not yet triggered. Consent and disclosure for this lane must be wired **before** any UI trigger is added.

**Three-part failure (updated):**
1. **Per-stage routing is opaque.** `CloudAware*` wrappers route silently by alias — no pre-run disclosure.
2. **No consent gate exists** for any cloud egress path, per-stage or full-pipeline.
3. **Cloud dubbing lane has no consent hook at all** — it's the most severe egress (full video file) and its wiring is deferred, creating risk that it gets connected before consent is designed in.

**Two-part failure:**
1. **Selection → routing is opaque.** `CloudAwareTranslationEngine`/`CloudAwareTtsEngine` choose an engine by matching `PreferredModelAlias` against static predicates (`IsDeepLModelAlias`, `IsElevenLabsAlias`, …). No pre-run disclosure shows which stages will use cloud.
2. **No consent gate.** `StudioSettings` has `AmdRyzenAiLicenseAccepted`, `NvidiaTensorRtRtxLicenseAccepted`, etc. for local EP licenses — but **zero consent fields** for cloud data egress. ASR sends a full audio file to OpenAI with no acknowledgment that this will happen.

**ASR is qualitatively different.** Translation/TTS egress derived text. ASR egresses **raw speech audio** — the actual voice from the source video, before any processing. This distinction must be preserved in the consent model; a flat "allow cloud" toggle collapses it.

---

## 2. Goals / Non-goals

**Goals**
- User knows, before pressing Run, exactly which stages will use cloud and what data category will leave the machine.
- Consent is obtained **once per `(egress-type, provider)` pair**, persisted in `StudioSettings`, and not re-prompted unless revoked.
- The consent model correctly distinguishes **audio egress** from **text egress** — even for the same provider (OpenAI is used for both ASR/audio and GPT-Translation/TTS/text).
- No cloud engine fires without valid consent and a set API key.
- Headless/SDK path has the same gate (no silent cloud calls).

**Non-goals**
- Key management UI — stays in `ApiKeyStore` / Settings `ApiKeysDialog`.
- Network-level key validation — stays in `ApiKeyEntryViewModel.ValidateKeyAsync` (on-demand, not blocking the panel).
- Privacy policy authorship — spec points to where URLs are declared; writing them is out of scope.
- Translation/ASR engine selection UI — choosing *which* cloud engine is out of scope.

---

## 3. Egress type taxonomy

Three consent buckets, not one:

```
EgressType.Audio   — raw audio bytes sent to provider (ASR stage)
EgressType.Text    — transcript or translated text sent to provider (Translation, TTS stages)
EgressType.Media   — entire source media file (video+audio) sent to provider (cloud-dubbing lane)
```

Consent key: `"{egressType}:{providerKey}"`, e.g. `"audio:openai"`, `"text:deepl"`, `"text:elevenlabs"`, `"media:elevenlabs"`.

Full matrix:

| Consent key | Lane | Stage | Provider | Data |
|---|---|---|---|---|
| `audio:openai` | per-stage | ASR | OpenAI Whisper | full audio file |
| `audio:gemini` | per-stage | ASR | Gemini | full audio file |
| `text:deepl` | per-stage | Translation | DeepL | transcript text |
| `text:openai` | per-stage | Translation + TTS | OpenAI GPT + TTS | transcript/translated text |
| `text:gemini` | per-stage | Translation | Gemini | transcript text |
| `text:elevenlabs` | per-stage | TTS | ElevenLabs TTS | translated text + voice ID |
| `text:google` | per-stage | TTS | Google TTS | translated text |
| `media:elevenlabs` | **cloud-dub** | Full pipeline | ElevenLabs Dubbing | **entire source media file** (video+audio) |

Note: `audio:openai` and `text:openai` are **separate consents** — audio egress is distinct from text egress, even to the same provider.

Note: `media:elevenlabs` is the most severe consent key — it gates the cloud dubbing lane. Because `ICloudDubbingEngine.DubAsync` is not yet wired to a UI trigger, this key has no consent prompt today. It **must be gated** before any trigger is added.

---

## 4. `StudioSettings` extension

Add to the `StudioSettings` record (parallel to existing `*LicenseAccepted` booleans, using the same persisted-settings pattern but in a dictionary to avoid N new fields):

```csharp
IReadOnlyDictionary<string, bool>? CloudEgressConsents = null
```

Key format: `"{egressType}:{providerKey}"` (lowercased). `null` / absent key = not consented. Revocation sets the key to `false` or removes it.

Helper (in Application or Contracts):
```csharp
public static class CloudEgressConsentKeys
{
    public const string AudioOpenAi      = "audio:openai";
    public const string AudioGemini      = "audio:gemini";
    public const string TextDeepL        = "text:deepl";
    public const string TextOpenAi       = "text:openai";   // GPT translation + TTS
    public const string TextGemini       = "text:gemini";
    public const string TextElevenLabs   = "text:elevenlabs";
    public const string TextGoogle       = "text:google";
    public const string MediaElevenLabs  = "media:elevenlabs"; // cloud dubbing lane — full video+audio file

    public static string Build(EgressType type, string providerKey) =>
        $"{type.ToString().ToLowerInvariant()}:{providerKey.ToLowerInvariant()}";
}

public enum EgressType { Audio, Text, Media }
```

---

## 5. `CloudEgressDescription` — what gets shown in the consent dialog

```csharp
public sealed record CloudEgressDescription(
    string ConsentKey,             // "audio:openai"
    EgressType EgressType,         // Audio | Text
    string ProviderDisplayName,    // "OpenAI"
    string StageDisplayName,       // "Transcription (ASR)"
    string DataDescription,        // "The full audio from your source video"
    string SentTo,                 // "api.openai.com/v1/audio/transcriptions"
    string? PrivacyPolicyUrl);     // "https://openai.com/policies/privacy-policy"
```

One `CloudEgressDescription` per consent key. Declared as a static catalog (Application layer — no inference/cloud code here, just metadata):

```csharp
public static class CloudEgressCatalog
{
    public static readonly IReadOnlyList<CloudEgressDescription> All = [ ... ];

    public static CloudEgressDescription? Find(string consentKey) =>
        All.FirstOrDefault(d => d.ConsentKey == consentKey);
}
```

---

## 6. Consent flow — when and how

### 6a. Proactive: at engine selection

When the user changes a stage's model override to a cloud alias (in Settings or Run Config), immediately:

1. Compute which consent key(s) the new selection requires.
2. For each key not yet consented: show `CloudEgressConsentDialog` (one per key, sequentially).
3. `CloudEgressConsentDialog` content: egress type badge (Audio 🎙 / Text 📝), provider name, data description, endpoint, privacy policy link, **[Allow] [Not now]** buttons.
4. On **Allow**: persist `CloudEgressConsents[key] = true` via `IStudioSettingsService.SaveAsync`.
5. On **Not now**: selection is NOT reverted (user can keep it), but consent remains absent → stage will be blocked at pre-run backstop.

### 6b. Reactive backstop: pre-run (G5 readiness gate)

`IPipelineReadinessService.EvaluateAsync` adds `CloudEgressConsentRequired` to `ReadinessState`:

- For each enabled stage: if cloud alias selected → look up required consent key(s) → check `StudioSettings.CloudEgressConsents`.
- Status `CloudEgressConsentRequired` → panel badge "☁🔒 consent needed" → resolve action opens `CloudEgressConsentDialog`.
- Run **blocked** while any stage has `CloudEgressConsentRequired` (same backstop logic as `CloudKeyMissing`).

### 6c. Pre-run disclosure strip (non-blocking if consented)

Configure panel shows a per-stage egress summary even when consent is already given — transparency, not a gate:

```
┌─ Run will send data externally ────────────────────────────┐
│  🎙 ASR → OpenAI  (audio)              ✓ consented         │
│  📝 Translation → DeepL  (text)        ✓ consented         │
│  📝 TTS → ElevenLabs  (text)           ✓ consented         │
└────────────────────────────────────────────────────────────┘
```

Only shown when ≥1 cloud engine is active. Zero cloud stages → strip hidden.

---

## 7. Updated `ReadinessState` (extends G5 §5)

G5's state table gains two new entries:

| State | Panel badge | Resolve action |
|---|---|---|
| `CloudKeyMissing` *(G5)* | ☁⚠ "set API key" | API keys dialog |
| `CloudEgressConsentRequired` *(G3 new)* | ☁🔒 "consent needed" | `CloudEgressConsentDialog` |

Both block Run. `CloudEgressConsentRequired` resolves *before* `CloudKeyMissing` in display order (consent is a prerequisite to needing a key — no point entering a key for a provider you haven't consented to use).

---

## 8. `ICloudEgressConsentService` (Application layer)

```csharp
public interface ICloudEgressConsentService
{
    /// <summary>
    /// Returns true if the user has previously consented to this egress key.
    /// Pure read — no I/O beyond reading cached settings.
    /// </summary>
    bool HasConsent(string consentKey);

    /// <summary>
    /// Returns the set of consent keys required for the given stage+alias selection.
    /// </summary>
    IReadOnlyList<string> GetRequiredConsentKeys(string stageName, string? modelAlias);

    /// <summary>
    /// Persists consent (or revocation) for a single key.
    /// </summary>
    Task SetConsentAsync(string consentKey, bool consented, CancellationToken ct);
}
```

Implementation lives in Application, reads/writes `StudioSettings.CloudEgressConsents` via `IStudioSettingsService`. No network calls. Injected into:
- `IPipelineReadinessService` — for stage evaluation
- App VM — for proactive consent at selection time
- Cloud engine wrappers — for assert-only guard (defense-in-depth, see §9)

`GetRequiredConsentKeys` is the lookup table: stage name × alias predicate → consent key(s). Uses the existing static predicates (`AsrModelOverrideSettings.IsCloudAlias`, `TranslationModelOverrideSettings.IsDeepLModelAlias`, etc.) — no new routing logic, just maps them to consent keys.

---

## 9. Defense-in-depth: cloud engine guard

Each cloud engine (`OpenAiCloudTranscriptionEngine`, `DeepLCloudTranslationEngine`, `ElevenLabsCloudTtsEngine`, …) gets an injected `ICloudEgressConsentService` and a **non-interactive assert** at the top of `TranscribeAsync`/`TranslateAsync`/`SynthesizeAsync`:

```csharp
if (!_consentService.HasConsent(_consentKey))
    throw new CloudEgressConsentException(
        $"Cloud egress to {ProviderName} has not been consented. " +
        "Resolve consent in Settings before running this stage.");
```

This is a safety net — the readiness gate and proactive consent should prevent reaching here. Matches "never fake readiness" and "surface encountered problems" invariants.

---

## 10. Headless/SDK path

`TrackdubDubbingEngine` (SDK) gets the same gate via `IPipelineReadinessService`:

- `EvaluateAsync` now also checks consent keys for cloud aliases.
- `RunPreFlightChecksAsync` maps `CloudEgressConsentRequired` → hard fail (add to `preFlightFailures`): `"ASR (audio:openai): cloud egress consent required. Set TRACKDUB_CLOUD_EGRESS_CONSENT=audio:openai,text:deepl or use the app to consent interactively."`
- **Headless consent override:** `DubbingSessionOptions` gains `IReadOnlyList<string>? ExplicitCloudEgressConsents`. If provided, those keys are treated as consented for the run (CI/automation use case — the caller opts in explicitly in code/config rather than via GUI).
- Without `ExplicitCloudEgressConsents` and without persisted GUI consent, headless run with cloud alias **fails fast** before stage loop. No silent audio upload.

---

## 11. Components by layer

| Layer | Change |
|---|---|
| `Trackdub.Domain` / `Trackdub.Contracts` | `EgressType` enum; `CloudEgressDescription` record; `CloudEgressConsentKeys` + `CloudEgressCatalog` static class; `ICloudEgressConsentService`; `CloudEgressConsentException`. |
| `Trackdub.Application` | `CloudEgressConsentService` impl; extend `IPipelineReadinessService.EvaluateAsync` to check consent per stage; extend `ReadinessState` with `CloudEgressConsentRequired`. |
| `Trackdub.Infrastructure` | Each cloud engine (`OpenAiCloud*`, `DeepLCloud*`, `ElevenLabsCloud*`, `GeminiCloud*`) gets assert guard via injected `ICloudEgressConsentService`. |
| `Trackdub.Composition` | Register `ICloudEgressConsentService`; wire into cloud engines and readiness service. |
| `Trackdub.Sdk` | `DubbingSessionOptions.ExplicitCloudEgressConsents`; pre-flight maps `CloudEgressConsentRequired` to fail. |
| `Trackdub.App.Avalonia` | `CloudEgressConsentDialog` (new view/VM); proactive consent at selection change; disclosure strip in Run Config panel; consent revocation in Settings/ApiKeys area. |

Layer boundaries preserved: no consent logic in VM beyond calling `ICloudEgressConsentService`; no cloud engine code in App.

---

## 12. Consent catalog (full, at spec time)

| Key | Lane | Stage | Provider | Data description | Endpoint | Privacy URL |
|---|---|---|---|---|---|---|
| `audio:openai` | per-stage | ASR | OpenAI Whisper | Full normalized audio file (WAV/MP3) | `api.openai.com/v1/audio/transcriptions` | https://openai.com/policies/privacy-policy |
| `audio:gemini` | per-stage | ASR | Google Gemini | Full normalized audio file | Gemini AI API | https://policies.google.com/privacy |
| `text:deepl` | per-stage | Translation | DeepL | Transcript text segments | `api.deepl.com/v2/translate` | https://www.deepl.com/en/privacy |
| `text:openai` | per-stage | Translation + TTS | OpenAI GPT + TTS | Transcript/translated text | `api.openai.com/v1/chat/completions`, `.../audio/speech` | https://openai.com/policies/privacy-policy |
| `text:gemini` | per-stage | Translation | Google Gemini | Transcript text segments | Gemini AI API | https://policies.google.com/privacy |
| `text:elevenlabs` | per-stage | TTS | ElevenLabs TTS | Translated text per segment + voice ID | `api.elevenlabs.io/v1/text-to-speech` | https://elevenlabs.io/privacy |
| `text:google` | per-stage | TTS | Google TTS | Translated text per segment | Google TTS API | https://policies.google.com/privacy |
| `media:elevenlabs` | **cloud-dub** | Full pipeline | ElevenLabs Dubbing | **Entire source media file** (video+audio bytes, MP4/MKV/WAV) | `api.elevenlabs.io/v1/dubbing` | https://elevenlabs.io/privacy |

*Catalog is code, not config — update when providers/endpoints change.*

---

## 13. Build sequence (phased)

1. **Contracts/Domain** — `EgressType`, `CloudEgressDescription`, `CloudEgressConsentKeys`, `CloudEgressCatalog`, `ICloudEgressConsentService`, `CloudEgressConsentException`, `StudioSettings.CloudEgressConsents`.
2. **Application** — `CloudEgressConsentService` impl; `ReadinessState.CloudEgressConsentRequired`; extend `EvaluateAsync`.
3. **Infrastructure** — assert guards in each cloud engine.
4. **Composition** — registration.
5. **SDK** — `ExplicitCloudEgressConsents` option; pre-flight fail mapping.
6. **App** — `CloudEgressConsentDialog` + disclosure strip; proactive consent at selection; revocation in Settings.

---

## 14. Tests

- `HasConsent` returns false for absent/null key; true only for explicit `true` in dict.
- `GetRequiredConsentKeys` returns correct key(s) for each cloud alias (covers all 7 combos).
- `EvaluateAsync` returns `CloudEgressConsentRequired` when consent absent; `Ready`/`CloudKeyMissing` when consented but key absent.
- `CloudEgressConsentRequired` blocks pre-run; `ExplicitCloudEgressConsents` unblocks headless.
- Cloud engine guard throws `CloudEgressConsentException` when consent absent (defense-in-depth path, distinct from `InvalidOperationException` for missing key).
- Consent persisted → `LoadAsync` round-trip preserves dict; revocation sets `false`.
- **ASR and text-OpenAI are independent:** consenting `text:openai` does NOT satisfy `audio:openai`.

---

## 15. Open questions

- **Revocation UX:** where does "withdraw consent" live? Settings > API Keys area is the natural home (already has per-provider controls). Confirm with product.
- **`ExplicitCloudEgressConsents` in headless:** should it also accept a file/env-var form for Docker/CI deployments? e.g. `TRACKDUB_CLOUD_EGRESS_CONSENTS=audio:openai,text:deepl`. Low-risk addition; note for SDK phase.
- **Gemini ASR engine:** `GeminiCloudTranscriptionEngine` exists but wasn't read. Assumed same audio-bytes egress as OpenAI (`audio:gemini`). Confirm during Phase 3.
- **Cloud dubbing lane UI trigger:** `ICloudDubbingEngine.DubAsync` has no callsite yet. When a trigger is added it MUST: (1) check `media:elevenlabs` consent first, (2) surface the full-file egress disclosure (this is more severe than TTS text), (3) show that cloud dubbing skips the editable transcript/speaker/segment review steps — ElevenLabs' internal transcript never returns to Trackdub; dubbed audio is muxed directly onto the original video via FFmpeg. Final output is still MP4/MKV. This spec gates the consent architecture; trigger wiring is out of scope here but consent MUST precede it.
- **Cloud dubbing lane + G5 readiness gate:** cloud dub bypasses `DefaultStageOrder` entirely. The G5 `IPipelineReadinessService` targets the per-stage pipeline. A separate readiness check (key present + consent given for `media:elevenlabs`) is needed for the cloud dub path before any trigger is added.
