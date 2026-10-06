# Design Spec — L1-TTS: Piper Ultra-Light CPU TTS Tier (Benchmark-First)

**Pitch status:** proposed — seeking product sign-off before any code.
**One-liner:** measure Kokoro on the target low-end CPUs first; add a piper-class fallback tier only if that evidence shows a real gap, with fallback selection owned by the runtime planner and Piper's embedded eSpeak-NG phonemizer/data packaged and verified like any bundled runtime dependency.

---

## 1. Problem — restated as an open question, not an assumed gap

The shipped CPU TTS tier is `KokoroTtsEngine` (ONNX on the CPU provider, espeak-ng phonemization via `EspeakNgPathResolver`/`EspeakNgHealthCheck`/`espeak-ng-data`). Two failure concerns were raised in earlier drafts:

1. **Speed on very weak machines** — no repo measurements exist for Kokoro real-time-factor on the target low-end CPU class.
2. **espeak-ng dependency fragility** — the health check exists because this chain can break; when it does, the CPU user has no working local TTS.

This pitch does **not** assume either is a live problem. It proposes benchmarking Kokoro first (Phase 0), and only on evidence designing a fallback tier (Phase 1). Piper-class TTS is the candidate: ultra-light voices that synthesize faster than real time on almost any CPU.

## 2. Goals / Non-goals

**Goals**
- Phase 0: measured Kokoro RTF and intelligibility on reference low-end hardware, plus a documented characterization of real-world espeak-ng failure cases.
- Phase 1 (conditional): a piper fallback tier with health-aware selection owned by the runtime planner, fully verified runtime and licensing provenance.

**Non-goals**
- Replacing Kokoro as the CPU default — piper is a fallback, not an upgrade.
- Voice cloning, quality parity with GPU tiers — this is an intelligible-voice-of-last-resort tier.
- New UI; the fallback is surfaced through the existing readiness/stage summaries.

## 3. Phase 0 — benchmark before design

- **Kokoro RTF on target CPUs** (DubBench, reference low-end machine profile, fixed voice + sentence suite): if RTF is comfortably < 1 on the target class, the speed concern is closed and only the dependency-fragility concern can justify Phase 1.
- **Piper comparison:** run the same suite against piper on the same hardware so the trade (RTF vs naturalness) is quantified, not asserted.
- **espeak-ng failure characterization:** what actually breaks in the field (missing `espeak-ng-data`, library ABI drift, distro packaging)? This determines whether a fallback tier or a hardening fix is the right response.

**Decision matrix:**

| Phase 0 result | Action |
|---|---|
| Kokoro RTF fine, no real fragility evidence | Close the spec — no new runtime |
| Kokoro RTF fine, fragility real | Prefer hardening Kokoro's espeak-ng path (bundle `espeak-ng-data`, tighten the health check); piper only if hardening fails |
| Kokoro RTF unacceptable on target class | Phase 1: piper fallback tier |

## 4. Phase 1 (conditional) — piper tier, planner-integrated

**Fallback selection belongs to the runtime planner.** TTS selection already flows through `RoutedTtsEngine` → `StageRuntimePlanningRequest`/`RuntimePlanner`; the piper engine family joins `StageRuntimeRequirements` for TTS as an allowed family on the CPU provider, and the planner makes the Kokoro-vs-piper call under its existing rules (with piper ranked below Kokoro). No engine-internal fallback logic and no second selection path — the planner is the single decision point, consistent with how every other stage routes.

**The embedded eSpeak-NG question — must be resolved, not assumed.** Piper embeds eSpeak-NG for phonemization. That means it addresses Kokoro's espeak failure **only if** its own phonemizer and `espeak-ng-data` equivalent are:
1. **Packaged with the sidecar** — no reliance on system eSpeak-NG or a distro package;
2. **Verified at readiness time** with the same health-check rigor as Kokoro's `EspeakNgHealthCheck` (data directory present, version pinned, phonemization smoke test), so a broken phonemizer surfaces as an explicit NotReady state instead of garbage output.

If piper's standalone binary packages its phonemizer data self-containedly (to be verified during Phase 0/1 investigation — per its README it embeds eSpeak-NG), it genuinely de-risks the fragility concern; if not, it inherits the same failure class and the case for it weakens to speed only.

**Sidecar pattern:** managed child process, loopback-only, health-checked startup, deterministic shutdown — same pattern proposed for the llama.cpp tier in `design-llamacpp-cpu-text-tier.md`.

## 5. Licensing and governance (blocking for Phase 1)

Per repo model governance (`AGENTS.md`: commercial-only, unknown license = unsafe; manifest schema requires `commercial_use_verified` per entry) and Piper's own docs (README notes embedded eSpeak-NG; voice model cards must be reviewed per voice):

- **Runtime:** piper binary + embedded eSpeak-NG license review (GPL-family implications of eSpeak-NG embedding for a commercial product must be cleared by legal before any bundling decision).
- **Voices:** per-voice model-card review; each voice is a manifest entry with license, `commercial_use_verified`, attribution flags — unverified voices are excluded.
- **No end-user runtime dependencies:** the piper sidecar must be a self-contained native binary (no Python in end-user paths). Piper's Python package is a development-time tool only; the C++ inference core is the integration target. If a standalone non-Python binary cannot be supported upstream, that is a spec blocker.

## 6. Execution plan

1. **Phase 0 (the ask):** Kokoro vs piper benchmark on reference low-end hardware + espeak-ng failure characterization + piper packaging/license investigation (self-contained binary? embedded-data licensing?).
2. **Phase 1 (conditional):** `PiperTtsEngine` + planner wiring (TTS requirements entry, ranked below Kokoro) + voice manifest entries with verified licenses + readiness health checks mirroring the Kokoro espeak pattern.
3. **Intelligibility gate:** fixed sentence suite per voice, native-listener review — sufficient for a fallback tier; plus DubBench RTF on the reference machine.

## 7. Risks

| Risk | Mitigation |
|---|---|
| Phase 0 shows no gap | Spec closes cheaply; no new runtime or dependency ships |
| eSpeak-NG (GPL) embedding blocks commercial bundling | Legal review is a Phase 0 exit criterion, not a late discovery |
| Piper inherits rather than fixes the phonemizer fragility | Packaging + health-check verification (§4) is a hard Phase 1 gate; if it fails, only the speed case remains and must clear the RTF bar on its own |
| Users land on piper without noticing | Planner-level selection is surfaced through existing readiness/stage summaries |
| Thin voice/language coverage | One verified voice per supported language in v1; catalog growth is manifest-only |

## 8. Open questions for review

1. Reference low-end hardware profile(s) — same question as the llama.cpp spec; ideally one shared profile set.
2. Upstream piper standalone-binary support (no Python) — confirm feasibility with the project before Phase 1.
3. Fallback trigger semantics in the planner: strict ranking (piper only when Kokoro NotReady) vs RTF-threshold demotion — decided after Phase 0 numbers.
4. If espeak-ng fragility proves real but Kokoro RTF is fine: is bundling `espeak-ng-data` with Kokoro (hardening) the whole answer, making piper unnecessary?
