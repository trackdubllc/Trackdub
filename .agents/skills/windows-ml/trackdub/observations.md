# Trackdub observations

Tier 3. These are things seen on Trackdub machines. None is a platform fact: some may come from Trackdub's own configuration, export choices or bugs. Treat each as a lead to re-test. Before code relies on one, reproduce it under the current versions and update its entry.

## Entry format

Every entry records:
- **Observed:** date, and the agent or person.
- **Setup:** OS, GPU and driver, package versions, model or export, how it was run.
- **Result:** what was seen, quoting the decisive line.
- **Reproduced:** how many times, under which variations.
- **Cause confidence:**
  - *confirmed*: cause found and fix verified;
  - *suspected*: plausible cause, not isolated;
  - *unknown*: not investigated;
  - *contradicted*: a higher tier disagrees;
  - *not applicable*: the entry records a state, not a failure.
- **Status:**
  - *current*: still relied on;
  - *needs re-test*: cause not settled, or versions moved on;
  - *misconfiguration*: Trackdub's own setup caused it;
  - *superseded*: no longer applies.
- **Acted on in:** files or PRs that encode a decision based on it, or "nothing".

Every field is required; write "unknown" or "nothing" rather than leaving one out. Add entries; do not rewrite history. When re-testing, append a dated result to the entry.

---

### O-1. A foreign `onnxruntime.dll` removed DirectML from the desktop

- **Observed:** 2026-10-07, Claude Code session.
- **Setup:** Desktop app (Trackdub-gated). A build target, `AlignOnnxRuntimeNativeForGenAi`, copied stock ORT 1.30 over Windows ML's `onnxruntime.dll`.
- **Result:** Every DirectML stage silently ran on CPU. `GetEpDevices()` listed no `DmlExecutionProvider`.
- **Reproduced:** Yes. The fix (moving GenAI to `.WinML` and deleting the target) restored DirectML, measured end to end.
- **Cause confidence:** confirmed.
- **Status:** misconfiguration (Trackdub build step), now fixed.
- **Acted on in:** #419; Trackdub-gated branch `feat/winml-2.4-desktop`.
- **Lesson worth keeping:** any build step that copies `onnxruntime*.dll` into an output folder can replace Windows ML's runtime. Learn's warning against mixing GenAI flavors (Tier 1) points the same way.

### O-2. GenAI Whisper and Qwen2.5 failed on DirectML

- **Observed:** 2026-10-07, Claude Code session.
- **Setup:** Windows ML 2.4.89 (ORT 1.27.1), GenAI.WinML 0.17.1. Probe in `D:\tdhost\gp`. Trackdub's bundled GenAI exports, which were built for CPU/CUDA, not DirectML. RTX-class GPU.
- **Result:**
  - Whisper ended the host process inside `Generator.SetInputs`.
  - Qwen2.5 failed its first DirectML kernel with `DmlFusedNode` 0x80070057.
- **Reproduced:** On one machine, one export each. Not tried with DirectML-targeted exports or other drivers.
- **Cause confidence:** suspected. The exports were never built for DirectML, so this may say more about the exports than about GenAI on DirectML.
- **Status:** needs re-test before relaxing the exclusion.
- **Acted on in:** `GenAiProviders` in `StageRuntimeRequirements.cs`; the DirectML refusal in `OnnxExecutionProviderSmokeTester.cs`.

### O-3. Only the uppercase `DML` provider spelling took effect in GenAI

- **Observed:** 2026-10-07, Claude Code session.
- **Setup:** Same probe as O-2. `config.SetProviderOption("DML", "enable_graph_capture", "0")` versus lower-case `dml`.
- **Result:** The option appeared to take effect only with `DML`.
- **Reproduced:** Once.
- **Cause confidence:** unknown. GenAI main normalizes provider names case-insensitively ([upstream](../upstream/ort-and-genai-source.md)); 0.17.1's behaviour is unverified. The difference may have come from another variable in the probe.
- **Status:** needs re-test. Do not encode casing rules from this.
- **Acted on in:** nothing in code.

### O-4. GenAI on NvTensorRtRtx ended the host process

- **Observed:** before 2026-10-09 (recorded in the smoke tester's message), observer not recorded.
- **Setup:** Bundled GenAI models on the TRT-RTX EP.
- **Result:** A native stack overflow ended the host process.
- **Reproduced:** Unknown.
- **Cause confidence:** unknown.
- **Status:** needs re-test.
- **Acted on in:** `WithoutTensorRtFamilies` for GenAI in `StageRuntimeRequirements.cs`; the TRT refusal in `OnnxExecutionProviderSmokeTester.cs`.

### O-5. Full pipeline completed on Windows ML 2.4

- **Observed:** 2026-10-07, Claude Code session. Evidence lives in a local run folder, not in the repository.
- **Setup:** Windows ML 2.4.89, after the DirectML VRAM factor change (#418), on one RTX machine.
- **Result:**

  | Stage | Provider |
  | --- | --- |
  | VAD | DirectML |
  | Diarization | TRT-RTX |
  | ASR (qwen3) | DirectML |
  | GenAI | CPU |
  | Translation | CPU |
  | TTS | CPU |

  The run completed with no overrides.
- **Reproduced:** Once.
- **Cause confidence:** not applicable.
- **Status:** needs re-test. It goes stale as soon as pins, the planner or exports change, so re-run before quoting it.
- **Acted on in:** nothing; it was used as evidence in #419's description.

### O-6. Managed ORT 1.30 runs against native ORT 1.27.1 on Windows

- **Observed:** 2026-10-09, from the build configuration, not from a failure.
- **Setup:** On the Windows TFM, `Microsoft.ML.OnnxRuntime.Gpu` is referenced managed-only, with `ExcludeAssets="native"`; the native ORT is Windows ML's.
- **Result:** The pipeline works (O-5). No crash has been attributed to the version gap.
- **Reproduced:** not applicable; this is a configuration fact.
- **Cause confidence:** not applicable. The risk is explained in [upstream](../upstream/ort-and-genai-source.md). Nobody has inspected the output folder to confirm which managed copy ships ([upstream/packages.md](../upstream/packages.md)).
- **Status:** current. Open question: drop the stock managed package on Windows and use Windows ML's matched managed API?
- **Acted on in:** `src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj` (`ExcludeAssets="native"` on the stock package).

### O-7. Deep scratch paths broke native DLL loading

- **Observed:** 2026-10-07, Claude Code session.
- **Setup:** Probe executables built under a long temp path.
- **Result:** The native DLL loader failed. The same probe moved to `D:\tdhost\...` worked.
- **Reproduced:** Once.
- **Cause confidence:** suspected (path length).
- **Status:** current, as a practical tip only.
- **Acted on in:** nothing in code; probes since then have lived under `D:\tdhost\`.
