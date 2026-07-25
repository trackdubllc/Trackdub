# Implementation Plan — G3: Cloud Egress Visibility & Consent

**Source:** [design-g3-cloud-egress-visibility.md](design-g3-cloud-egress-visibility.md)

**Prerequisite:** G5 Phase 1–2 (Contracts + Application Evaluate + Panel) must land first. G3 builds on ReadinessState, IPipelineReadinessService, and the readiness panel.

---

## Phase 1: Contracts — Cloud consent model (1 day)

Add consent tracking + exceptions.

**Files:**
- src/Trackdub.Contracts/Cloud/EgressType.cs
- src/Trackdub.Contracts/Cloud/CloudEgressDescription.cs
- src/Trackdub.Contracts/Cloud/CloudEgressConsentKeys.cs
- src/Trackdub.Contracts/Cloud/CloudEgressConsentException.cs

**Logic:**
- EgressType enum: Audio, Text, Media
- CloudEgressDescription record: ConsentKey, EgressType, ProviderName, DataDescription, Endpoint, PrivacyPolicyUrl
- CloudEgressConsentKeys static: constants for all 8 consent keys + Build(type, providerKey)
- CloudEgressConsentException: throw when engine lacks consent

---

## Phase 2: Application — Consent service (2 days)

Track and query consent state.

**Files:**
- src/Trackdub.Contracts/Cloud/ICloudEgressConsentService.cs
- src/Trackdub.Application/Cloud/CloudEgressConsentService.cs
- src/Trackdub.Application/Cloud/CloudEgressConsentCatalog.cs (static)

**Logic:**
- ICloudEgressConsentService: HasConsent(key), GetRequiredConsentKeys(stage, alias), SetConsentAsync(key, consented, ct)
- Impl reads/writes StudioSettings.CloudEgressConsents dict
- Catalog: static 8-entry table (audio:openai, audio:gemini, text:deepl, text:openai, text:gemini, text:elevenlabs, text:google, media:elevenlabs)

---

## Phase 3: Composition — Registration (1 day)

Wire consent service into app + SDK.

**Files:**
- src/Trackdub.Composition/CompositionRoot.cs

**Logic:**
- Register ICloudEgressConsentService → CloudEgressConsentService
- Inject into IPipelineReadinessService (for consent probes)
- Inject into cloud engine instances (ElevenLabsCloudTtsEngine, OpenAiCloudTranscriptionEngine, etc.)

---

## Phase 4: Application — Readiness integration (2 days)

Extend G5's readiness to check consent.

**Files:**
- (extend) src/Trackdub.Application/Pipeline/PipelineReadinessService.cs
- (extend) src/Trackdub.Contracts/Pipeline/ReadinessState.cs (add CloudEgressConsentRequired)

**Logic:**
- EvaluateAsync: for each cloud alias, check consent via ICloudEgressConsentService.HasConsent(key)
- If missing consent, return CloudEgressConsentRequired state
- G5's readiness panel already renders per-stage badges; consent badge + resolve action follow existing pattern

---

## Phase 5: App — Consent dialog (2 days)

Proactive consent prompt on model selection + pre-run backstop.

**Files:**
- src/Trackdub.App.Avalonia/Views/CloudEgressConsentDialog.axaml
- src/Trackdub.App.Avalonia/ViewModels/CloudEgressConsentViewModel.cs

**Logic:**
- Show when cloud alias selected (post-selection, proactive)
- Display egress type (Audio 🎙 / Text 📝 / Media 🎬), provider, data description, endpoint, privacy link
- [Allow] [Not now] buttons
- On Allow: SetConsentAsync(key, true) → persist to settings
- On Not now: keep selection, but stage blocked at pre-run (backstop catches it)

---

## Phase 6: Infrastructure — Defense-in-depth (1 day)

Assert guards in cloud engines.

**Files:**
- (extend) src/Trackdub.Infrastructure/Tts/ElevenLabsCloudTtsEngine.cs
- (extend) src/Trackdub.Infrastructure/Transcription/OpenAiCloudTranscriptionEngine.cs
- (extend) src/Trackdub.Infrastructure/Translation/DeepLCloudTranslationEngine.cs
- (extend) src/Trackdub.Infrastructure/Dubbing/ElevenLabsCloudDubbingEngine.cs

**Logic:**
- Each engine: at top of Synthesize/Translate/Transcribe/Dub, assert HasConsent(consentKey)
- Throw CloudEgressConsentException if absent (safety net; gate prevents this path)

---

## Tests

- HasConsent returns false for absent key; true for explicit true in dict
- GetRequiredConsentKeys returns correct key(s) for each cloud alias
- EvaluateAsync returns CloudEgressConsentRequired when consent absent
- audio:openai and text:openai are separate (both required for OpenAI ASR + TTS pair)
- ConsentDialog persists consent to StudioSettings
- Engine assert throws CloudEgressConsentException when consent absent
