# Trackdub Windows ML Configuration Audit

Consolidated findings, suggested solutions, and verification plan.

- Audit date: October 10, 2026
- Mode: read-only
- Status: source review completed for inspected areas; runtime and release certification incomplete

## 1. Executive assessment

The Windows ML host / GenAI.WinML / separate CUDA worker architecture is reasonable. Inspection found incorrect or misleading treatment of installed-but-unprepared catalog providers, pending preparation results, and compiled-model cache validity. Managed/native ORT selection, publish isolation, and gated desktop integration remain verification risks rather than demonstrated runtime failures.

No code, settings, branches, provider installations, or GitHub records were modified. No builds, tests, fresh inference, DLL hashing, or publish validation ran. Historical pipeline observations are not fresh test results.

## 2. Scope and evidence

| Item | Audit baseline |
| --- | --- |
| Requested skill | PR #424: `.agents/skills/windows-ml/`; branch `docs/windows-ml-skill-tiers` |
| Skill head inspected through GitHub | `c6391df163873d5c668baa7dd7e5d19830dd8ee5` |
| Local core | `D:\Dev\Trackdub_Workspace\Trackdub`; `main` at `474726e9d426a9f3ef157105a5bd096315b4593c` |
| Baseline relationship | Local main matches PR #424 base. PR changes documentation/skill rather than production runtime wiring. Local working-tree cleanliness was not verified. |
| Local gated checkout | `D:\Dev\Trackdub_Workspace\Trackdub-gated`; branch `repin-main-46dead0f`. Not established as current remote gated main or the desktop upgrade branch. |
| Machine | B550F, online during resumed inspection. No fresh OS/GPU/driver or live module inventory captured. |
| Supporting evidence | PR #419 patches; PR #424 skill and observation ledger; local production files; Microsoft Learn lookup. |
| RAG limitation | Audit-specific lookup blocked by confirmation gate. Earlier successful lookup established TRT-RTX pin, not full current Windows ML configuration. |

### Evidence discipline

- Tier 1: Microsoft Learn platform guidance.
- Tier 2: version-pinned upstream implementation or package evidence.
- Tier 3: dated Trackdub observations, not platform contracts.
- Application-source inspection establishes behavior in files read, not successful execution on hardware.
- Skill's upstream ABI note was read, but not independently verified against the exact shipped binding.

## 3. Priority register

| ID | Priority / status | Finding |
| --- | --- | --- |
| F1 | P1 / source-confirmed behavior | `NotReady` providers blocked by no-download policy |
| F2 | P2 / source-confirmed | `InProgress` labeled failure; HRESULT/diagnostic text discarded |
| F3 | P1 / source-confirmed validation gap | TRT-RTX EP-context reuse lacks selected-device compatibility checks |
| F4 | P1 / unresolved output risk | Competing managed ORT bindings against Windows ML native ORT |
| F5 | P2 / source-confirmed policy gap | Ordinary ORT initialization permits unknown provenance |
| F6 | P1 / checkout-specific risk | Local gated desktop retains overwrite target and old pins |
| F7 | P2 / unverified deployment risk | Worker build isolation does not establish publish isolation |
| F8 | P2 / evidence debt | GenAI exclusions need export-specific revalidation |
| F9 | P2 / source-confirmed retry concern | Registration caches retain failed aggregate results |
| F10 | P2 / incomplete coverage | Automatic-policy and all-provider validation not finished |

## 4. Detailed findings and suggested solutions

### F1. Installed-but-unprepared providers blocked when downloads are disabled

**Priority/status:** P1. Code behavior confirmed; real-hardware occurrence not reproduced. Explicit preparation consent may be application policy, but must not be presented as missing installation or a required download.

**Evidence locations:**

- `src/Trackdub.Inference.Onnx/WindowsMl/WindowsMlExecutionProviderBootstrapper.Windows.cs`
- `src/Trackdub.Inference.Onnx/WinMlCatalog/WindowsMlCatalogEpRegistration.cs`
- `src/Trackdub.Inference.Onnx/Migraphx/WindowsMlMigraphxCatalogService.cs`

**Observed:** Installed-only bootstrap skips providers not already `Ready`. Generic registration refuses both `NotPresent` and `NotReady` when downloads are disabled. MIGraphX refuses `NotReady` and asks users to enable downloads. Microsoft distinguishes `NotReady` (installed, absent from app dependency graph) from `NotPresent` (not installed).

**Impact:** An installed provider can remain unavailable to a fresh process despite needing preparation rather than acquisition. MIGraphX also maps its `NotReady` blocker to `EpNotPresent`.

**Suggested solution:**

1. Separate acquisition permission from preparation of installed packages.
2. `NotPresent` plus downloads disabled: stop without acquisition.
3. `NotReady`: prepare installed package, subject to any separately documented preparation consent.
4. `Ready`: register.
5. If preparation is intentionally user-triggered, show “Prepare installed provider,” not download-required messaging.
6. Preserve hardware, OS, license, and registration gates.

**Acceptance tests:** All states with acquisition enabled/disabled; `NotReady` reaches registration without acquiring absent providers; provider visibility remains separate from real inference.

**Evidence basis:** Microsoft Learn installation/registration guidance; skill `canon/deployment-and-lifecycle.md`.

### F2. Pending preparation mislabeled as failure; diagnostics lost

**Priority/status:** P2. Confirmed in source; no pending/failed installation induced.

**Evidence locations:** Same three files as F1.

**Observed:** Every preparation status other than `Success` follows a failure path. `InProgress` becomes `EpDownloadFailed` or an “EnsureReadyAsync failed” message. Messages omit `ExtendedError` and `DiagnosticText`. MIGraphX also classifies internal timeout as download failure on downloads-disabled checks.

**Impact:** Pending preparation looks terminal. Logs cannot distinguish unfinished preparation, failed acquisition, failed registration, and read-only check timeout.

**Suggested solution:**

- Represent pending preparation explicitly; do not register before success.
- Support bounded later retry/refresh for `InProgress`, not busy-looping.
- Preserve provider, status, HRESULT, diagnostic text, phase, and timeout context for real failures.
- Classify MIGraphX no-download timeout as preparation/registration-check failure.

**Acceptance tests:** Success/pending/failure, caller cancellation, internal timeout; no registration on pending/failure; diagnostics survive application/UI projection.

**Evidence basis:** Microsoft Learn installation guidance; skill `canon/diagnostics.md`.

### F3. EP-context validity relies on stamps, not selected-device compatibility

**Priority/status:** P1. Gap confirmed in inspected reuse path; no stale artifact loaded experimentally.

**Evidence locations:**

- `src/Trackdub.Inference.Onnx/EpContext/EpContextLoadPathResolver.cs`
- `src/Trackdub.Inference.Onnx/EpContext/EpContextArtifact.cs`
- `src/Trackdub.Inference.Onnx/EpContext/EpContextCompiler.cs`
- `src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs`

**Observed:** Reuse checks architecture bucket, driver, bundled TRT-RTX fingerprint, and source/sidecar length plus modification time. Search across `Inference.Onnx` found no `GetCompatibilityInfoFromModel` or `GetModelCompatibilityForEpDevices` calls. Compilation checks EPContext nodes before publication, but not selected-device compatibility.

Additional gaps:

- Fingerprint omits native ORT version and compilation-option identity.
- `SourceSha256` is stored but not checked by `MatchesSource`.
- Compiler-created stamps pass null source hash.
- Unknown hardware/driver values can still match.

**Impact:** Stamp agreement and EPContext node presence do not establish current EP/device/runtime compatibility or exact source bytes. Same-size, same-timestamp replacement escapes this identity check.

**Suggested solution:**

1. Add source-content identity, including actual external weights. Reuse existing manifest/revision/hash infrastructure to avoid expensive repeated hashing.
2. Include runtime and normalized compile/provider-option identity.
3. Evaluate opaque compatibility metadata against a nonempty same-EP device group matching session selection.
4. Missing metadata, unsupported/not-applicable results, or validation errors become explicit cache misses unless a separately evidenced provider-specific policy justifies another path.
5. Choose optimal-only versus supported-but-recompilation-preferred reuse deliberately; the latter is not universally forbidden.
6. Preserve staging, full-sidecar publication, cross-process locks, and rollback. Validate fresh artifacts before promotion.

**Acceptance tests:** Runtime/driver/device/source/external-weight/option changes; every compatibility status; missing metadata/API failure; fresh compile/reuse/original fallback; missing sidecars; rollback; unknown hardware; stamp schema migration.

**Evidence basis:** Microsoft Learn compilation guidance; skill `canon/model-setup-and-configuration.md` and `canon/structure-and-edge-cases.md`.

### F4. Managed/native ORT alignment remains unverified

**Priority/status:** P1 verification priority. Competing dependency configuration confirmed; selected binary and unsafe API exposure unresolved.

**Evidence locations:**

- `Directory.Packages.props`
- `src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj`
- Skill `upstream/packages.md` and `upstream/ort-and-genai-source.md`

**Observed:** Windows pins Windows ML 2.4.89, GenAI.WinML 0.17.1, stock ORT 1.30.0. Stock GPU reference excludes native assets, not managed binding. Skill records that Windows ML also supplies `Microsoft.ML.OnnxRuntime.dll`. Windows ML native ORT is 1.27.1. Winning managed copy was not inspected.

```text
Trackdub C#
  -> Microsoft.ML.OnnxRuntime.dll  (managed binding)
  -> onnxruntime.dll              (native engine)
```

Two candidate managed bindings exist in the dependency graph: Windows ML's binding and stock ORT 1.30's binding. Native pairing verification for ORT and GenAI does not establish managed ORT binding identity.

**Impact:** Newer binding can expose functionality absent from older native runtime. Basic inference success would not prove every API safe. Skill's function-table/ABI concern is a version-specific lead, not an independently reproduced crash. Different version numbers alone do not prove incompatibility.

**Suggested solution:**

- Identify actual managed DLL by SHA-256/package provenance, not assembly version alone.
- Record loaded native path/version separately.
- Prefer Windows ML managed/native surfaces on Windows ML host if required API surface is available. Keep stock ORT in portable targets and separate CUDA worker.
- If mixture is intentional, verify exact upstream implementation and enforce documented API/version boundary.
- Do not replace desktop native runtime with stock GPU ORT simply to align numbers. Do not adopt experimental Windows ML solely for newer ORT.

**Acceptance tests:** Compile references/build/publish outputs; managed hashes against both packages; native identity; ordinary inference, discovery/selection, compilation/compatibility APIs, CPU GenAI generation; worker independently validated.

**Evidence basis:** Microsoft Learn runtime-version mapping; skill Tier 2 package/source notes, not fresh output verification.

### F5. Ordinary ORT loading accepts unknown provenance

**Priority/status:** P2. Enforcement difference confirmed; legitimate custom runtime flavors require consideration.

**Evidence locations:**

- `src/Trackdub.Inference.Onnx/Runtime/GenAiNativeRuntimeSelection.cs`
- `src/Trackdub.Inference.Onnx/WindowsMl/WindowsMlOnnxRuntimeNativeResolver.cs`

**Observed:** `EnsureOrtLoaded(bool requireVerified = false)` can load a binary without supported provenance. Windows ML resolver invokes ordinary path. GenAI strictly verifies native hashes/pairing. Neither verifies F4's managed binding selection.

**Impact:** Ordinary initialization can proceed with unknown provenance before GenAI rejects its own pairing. This weakens prevention of foreign-runtime regressions but may be intentional for other flavors.

**Suggested solution:** Define runtime identity policy per Windows ML desktop, CUDA worker, portable ORT, and DNNL. Require expected provenance on production Windows ML routes without imposing GenAI's pair policy everywhere. Log selected path/flavor/version/verification status; provenance is not successful inference.

**Acceptance tests:** Expected binary, replaced file, missing metadata, preloaded module, competing resolver, restricted enumeration, worker and DNNL exceptions.

**Evidence basis:** Local source; application policy, not Microsoft platform mandate.

### F6. Local gated desktop retains historical overwrite wiring

**Priority/status:** P1 checkout-specific risk. Not established as remote main or shipped regression.

**Evidence locations:**

- `Trackdub-gated/src/Trackdub.App.Avalonia/Trackdub.App.Avalonia.csproj`
- `Trackdub-gated/Directory.Packages.props`

**Observed:** Local branch `repin-main-46dead0f` retains `AlignOnnxRuntimeNativeForGenAi`, copying runtime-layout ORT over app root after build. Central pins include ORT 1.24.4, Windows ML 2.0.300, and Windows App SDK ML/Runtime 1.8-era references. Skill's upgrade branch reportedly removed target; these are different branches.

**Impact:** This desktop checkout is not demonstrably aligned with upgraded core. Actual overwrite depends on source runtime-layout file and resolved graph. Output identity was not measured.

**Suggested solution:** Resolve intended gated branch and exact core submodule revision; inspect evaluated package graph; port WinML/GenAI.WinML change and remove conflicting target where applicable; build in approved isolated output without touching unrelated changes.

**Acceptance tests:** Gated/core commits and lock graph; app-root identity before/after build/publish; DirectML visibility and real run; CPU GenAI generation.

**Evidence basis:** Local gated source; skill observation O-1 describes historical regression and upgrade branch.

### F7. Worker build isolation does not prove publish isolation

**Priority/status:** P2. Verification gap, not confirmed publish defect.

**Evidence locations:**

- `src/Trackdub.InferenceWorker/Trackdub.InferenceWorker.csproj`
- `src/Trackdub.Composition/Trackdub.Composition.csproj`

**Observed:** Worker overwrites root with stock GPU native DLLs after build. Composition copies worker output under `inference-worker/`, marking worker content for publish. No direct-worker or desktop publish inspected. ARM64 has no stock CUDA payload on documented package path.

**Impact:** Correct build layout does not prove native selection in every publish route.

**Suggested solution:** Validate direct-worker and full desktop publish; make selection explicit in publish items if tests reveal gap; prevent worker native files reaching app root; verify architecture-specific behavior; update stale worker comments still describing GenAI hosting after ADR amendment narrowed role.

**Acceptance tests:** Hash/inventory app root, worker root, runtime layout; x64 worker handshake/Kokoro CUDA smoke; ARM64 honestly reports CUDA unavailable without app contamination.

**Evidence basis:** Local projects; skill package notes and ADR-0017.

### F8. GenAI exclusions need export-specific revalidation

**Priority/status:** P2 evidence debt. Keep guards until safe alternative proven.

**Evidence locations:**

- `src/Trackdub.Inference/Runtime/Planning/StageRuntimeRequirements.cs`
- `src/Trackdub.Inference.Onnx/Runtime/Planning/OnnxExecutionProviderSmokeTester.cs`
- Skill `trackdub/observations.md`

**Observed:** DirectML failures are one-machine observations using CPU/CUDA-targeted exports. TRT-RTX cause/reproduction incompletely recorded. Broad refusals encode product policy partly from these observations. Provider-name casing is disputed and unverified for pinned release.

**Impact:** Export failures can become alleged platform restrictions. Relaxing guards blindly risks host termination.

**Suggested solution:** Retain crash guards; label restrictions as approved-export/configuration policy; retest appropriate exports in isolated child processes; avoid uppercase-only naming rules from disputed evidence; append dated retest evidence instead of rewriting history.

**Acceptance tests:** Exact export/hash/runtime/GenAI/EP/driver/options; repeated construction/generation outcomes; actual placement; representative model tests before allow-list changes.

**Evidence basis:** Skill O-2/O-3/O-4; PR #419 planner/smoke patches. No fresh hardware results.

### F9. Failed aggregate registration results persist in caches

**Priority/status:** P2 retry concern confirmed in policy; end-to-end recovery untested.

**Evidence location:** `src/Trackdub.Inference.Onnx/WindowsMl/WindowsMlProviderRegistrationPolicy.cs`

**Observed:** Completed-result caching stores results without requiring success. Ensure-all path returns cached failure. Readiness calls bypass their completed-result cache; session/global-install paths can reuse old outcomes. No invalidation method appears in inspected class.

**Impact:** Transient failures may persist after external conditions improve. Per-provider preparation might bypass this on some paths; complete call-chain impact remains to trace.

**Suggested solution:** Cache successes only or use bounded negative TTL; explicit invalidation after install/preparation; retain per-provider outcomes rather than inferring provider success from partial aggregate success; document restart-only recovery if intentional.

**Acceptance tests:** Failure then success in same process; partial success; refresh; session requests; explicit ensure-all retry; no unauthorized acquisition.

**Evidence basis:** Local source; derived review concern, not reproduced UI failure.

### F10. Automatic-policy and all-provider audit coverage incomplete

**Priority/status:** P2 coverage limitation; no policy-bypass defect established.

**Evidence locations:** Session factory, device policy mapper, provider-specific hardware gates/services/manifests/tests.

**Observed:** Inspected helper limits catalog policy eligibility to MIGraphX, QNN, VitisAI, and OpenVINO catalog routes. DirectML and standalone TRT-RTX classified separately. Selected-provider reporting exists. Full call chains, hardware gates, license consent, and model-family interactions not validated.

**Impact:** Requested policy does not prove selected hardware, allowed model routing, full acceleration, or successful inference.

**Suggested solution:** Keep Explicit baseline; trace every policy application and enforce manifest/family constraints; validate device classes separately; check current VitisAI driver eligibility and MIGraphX GenAI restrictions; do not copy options/precision claims across providers.

**Acceptance tests:** Actual selected provider/device and CPU fallback/partition evidence; hard pins, unavailable devices, exclusions, changing device lists, OS/hardware/driver/license gates, download consent.

**Evidence basis:** Skill all-provider guidance and local helpers. Remaining work, not certification.

## 5. Positive controls to preserve

- Direct Windows ML and GenAI.WinML pins, rather than minimum dependency alone.
- Standalone TRT-RTX EP ABI route, excluding that provider from catalog registration.
- No bulk catalog acquisition on session/readiness hot paths.
- GenAI native hashes, supported pairing, and loaded-path verification.
- DirectML probe checks loaded runtime rather than GPU name.
- Worker CUDA availability scoped to hosted engine family.
- TRT-RTX cached artifacts not selected for DirectML/CPU.
- Sidecar-aware publication, cross-process locking, rollback.
- Effective-provider reporting instead of requested-provider claims.
- Skill distinguishes runnable/recompile-preferred artifacts and weak observations.

These are source/documentation positives, not new hardware passes.

## 6. Recommended implementation order

1. Fix lifecycle/result handling F1/F2; deterministic fake-backed tests; retry recovery F9.
2. Harden cache identity and compatibility F3, preserving rollback safeguards.
3. Inspect managed/native binaries and settle host binding/provenance F4/F5.
4. Resolve gated branch/submodule and desktop/worker publish isolation F6/F7.
5. Isolated real-model revalidation and all-provider coverage F8/F10.

## 7. Evidence required before closing audit

| Evidence | Required result |
| --- | --- |
| Repository identity | Core/gated commits, submodule revision, working-tree status; unrelated changes preserved |
| Managed provenance | Actual managed ORT DLL matched to package by SHA-256 |
| Native identity | Loaded ORT/GenAI paths and versions match host/flavor |
| Build/publish isolation | WinML app root and stock CUDA worker remain separate |
| Catalog lifecycle | Correct NotReady, pending, error diagnostics, retry recovery |
| Provider execution | Representative real inference, not discovery/session construction alone |
| GenAI | CPU generation on intended pair; risky accelerator tests isolated |
| Compiled cache | Fresh compile/reuse, device/source validation, invalidation/fallback tested |
| Architecture coverage | x64 evidence not used to certify ARM64/other vendors |
| Test accounting | Missing fixtures/skipped integration tests recorded as skipped |

## 8. Source traceability and limitations

Platform evidence: Microsoft Learn installation, registration, runtime-version mapping, and compilation guidance checked during conversation. Skill's upstream/package observations require reinspection after version changes.

Repository evidence: PR #424 skill and PR #419 patches, followed by direct local reads at core baseline. Older project-file text described catalog-first TRT-RTX; inspected source and earlier RAG pin identify standalone plugin as current route. Application source takes precedence for that routing decision.

Report does not certify runtime readiness, legal compliance, commercial model rights, or whole-graph acceleration. It consolidates audit evidence, including retry concern derived from source already inspected; it is not a new exhaustive repository sweep.
