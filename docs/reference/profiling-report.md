# Trackdub performance profiling report

> **Status:** MIXED — controlled dubbing-pipeline samples are recorded below; reference-machine, startup, idle working-set, TRT-RTX EP rows measured 2026-10-06, audio-preparation matrix rows (cold ×5 isolated + cold ×5 compatible + warm ×32 + silence) measured 2026-10-07, and project-open, Model Manager, project/translation memory, export and waveform rows measured 2026-10-10 at core `eaeed1d9`. No `pending local run` row remains. The 2026-10-10 desktop rows come from a Debug-config (`Optimize=true`) build with the development trust ring because no signed revocation feed was available for a Release build, and the host was not quiet; read [Desktop project open, Model Manager and memory](#desktop-project-open-model-manager-and-memory-2026-10-10) before comparing them with the Release rows.
> **Last updated:** 2026-10-10
> **Report branch:** core performance-audit stack beginning at `17c4a66` (not the revision used for the older samples)

## Measurement methodology (fill before claiming budgets)

Use the same procedure on every run so rows in this report stay comparable.

| Step | Tool / command | Record in report |
|---|---|---|
| Cold startup | `Stopwatch` from `Main` entry to first interactive shell frame, or ETW/`dotnet-trace` | ms, TFM, commit SHA |
| Working set | Task Manager **Private working set** or `dotnet-counters monitor --counters System.Runtime` | MB after 5 min idle |
| Export throughput | Wall clock around export command; note FFmpeg profile and segment count | duration, real-time factor |
| SQLite plans | `dotnet test tests/Trackdub.Infrastructure.Tests --filter FullyQualifiedName~Explain` | pass/fail + index names |
| UI layout | `Trackdub.UI.Tests` layout facts; PNG only when `CAPTURE_UI_SCREENSHOTS=1` | test name + optional PNG path |
| Inference / export bench | `dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- --help` then targeted scenario | log path, model manifest IDs, EP policy |

**Rules:** never collapse provider registered, model downloaded, stage ran, and stage succeeded. Label every number as *measured on reference machine* or *pending local run*. Do not copy example rows below into release notes as real data.

## Controlled dubbing-pipeline evidence

The path-free source record, fixture hashes, commands, run semantics, and retained-report locations are documented in [Local benchmark evidence](../development/benchmark-evidence.md). The rows below are measured initial samples from 2026-09-23. Their producing source revision and build identity were not captured, so they are historical observations only: exclude them from like-for-like comparisons, comparison medians, and release budgets.

| Host field | Measured value |
|---|---|
| OS | Windows 10.0.26200 x64 |
| CPU | AMD Ryzen 7 5700X3D |
| Physical RAM | 68,613,902,336 bytes; benchmark host reported 63 GB available |
| GPU | NVIDIA GeForce RTX 5070 |
| Runtime | .NET 10.0.12; ONNX Runtime 1.30.0.0 |
| GPU memory | Unavailable; no reliable probe was active |
| Measured revision / build | Unknown; not recorded with these runs |

| Fixture / run id | Mode / measured stage | Outcome | Timed pipeline |
|---|---|---|---:|
| Short `40aaa1e374c2465f8bcde775ba08e323` | Fresh process, compatible cache / ASR | Completed; requested and actual CPU; `qwen3-asr-0.6b` | 15,510 ms |
| Silence `93114ca2f2ab4ec8866f45e1f4ab96f8` | Fresh process, isolated engine cache / audio preparation | Completed | 18,088 ms |
| Multi-speaker `e14ecc07377e43a7a7ce5fc4c0f29dec` | Fresh process, isolated engine cache / audio preparation | Completed | 15,564 ms |
| Long-form `06600a21eac14be787fb459a9ecc4e64` | Fresh process, isolated engine cache / audio preparation | Completed | 164,224 ms |
| Short `033e3082f4544fc7911feac78de9851c` | Warm host / audio preparation | Completed | 3,165 ms |
| Short `36b343baec5d4f86a1bce26c7c354520` | Artifact resume / audio preparation | Skipped: `EXISTING_ARTIFACTS_VALID`; excluded from throughput comparisons | 482 ms |

The focused ASR sample also spent 329,650 ms preparing prerequisites; that duration is separate from its 15,510 ms ASR span. Full-pipeline run `91b3193af93a49c9ae0310ebc700fa66` is excluded because translation failed, TTS/export were skipped, and its provider labels were invalidated. The samples do not claim cleared OS/driver caches, GPU-memory measurement, thirty-run warm tails, or a complete model/provider matrix.

### Revision-pinned replacement samples, 2026-09-28

These fresh local runs used a clean checkout of `4eea0cc954f745ada80cac2be9e8992f93851883`, a Release `net10.0-windows10.0.19041.0` build, and `dotnet run --no-build`. The benchmark host DLL SHA-256 was `F054C1A51B37C9F5C7EA2218CC4D66EE037708DD6404C02C2571B626F1113902`. All four fixture hashes matched the local manifest before measurement. The linked [source record](../development/benchmark-evidence.md#revision-pinned-replacement-samples-2026-09-28) lists the run commands and retained reports. The raw reports omit per-run model-manifest IDs and `WindowsMlExecutionDevicePolicy`, so the full per-run provenance rule below is not yet met. These are single samples per scenario, not comparison medians or evidence of a speedup over the unversioned September 23 runs.

| Fixture / run id | Mode / measured stage | Outcome | Timed pipeline |
|---|---|---|---:|
| Short `872e838f131844d1a8c612a7c761a31c` | Fresh process, compatible cache / ASR | Completed; requested and actual CPU; `qwen3-asr-0.6b` | 60,417 ms |
| Silence `4b65ad4c1658474c90c6128907f7355a` | Fresh process, isolated engine cache / audio preparation | Completed | 7,947 ms |
| Multi-speaker `d250be27e8e1429ca77199048ace0866` | Fresh process, isolated engine cache / audio preparation | Completed | 6,974 ms |
| Long-form `fd9b830caf3546aab9496c4a518a0f54` | Fresh process, isolated engine cache / audio preparation | Completed | 100,791 ms |
| Short `38bdf0786ae94d53aedfc2d0996e5bd1` | Warm host / audio preparation | Completed | 2,459 ms |
| Short `9c32c7f661a04b689d2683dac3b31cfe` | Artifact resume / audio preparation | Skipped: `EXISTING_ARTIFACTS_VALID`; excluded from throughput comparisons | 389 ms |

The ASR run spent another 30,251 ms preparing prerequisite stages; that duration is separate from the 60,417 ms timed ASR pipeline. Cache and OS state were not normalized across the historical and replacement runs. Repeat each mode in independent processes before treating these numbers as a stable performance baseline.

### Audio-preparation matrix, 2026-10-07

Measured on the reference machine at the `46dead0f` working tree (clean, Release `net10.0`, ORT 1.30.0.0, .NET 10.0.12, RTX 5070 12227 MiB driver 617.14; OS caches not cleared). Fixture: short.mp4 `c4640c3f8062b4d928eeef25c52f845f4867c10f26aeb4f5d5ce6be1c295bd85` (matches manifest), silence `e7df589267ecde30673ffcdf9da443f56ea1e698b2d6c71ac637d31d501ec5eb`. Stage measured: `audio-preparation` (no engine cache involved — cache-cold and cache-warm are statistically indistinguishable). Raw data, per-run reports, and provenance are host-local and not committed to this repository (retained on the reference machine under `c7-evidence/2026-10-07-step22/`: `STEP22-RUN-NOTES.md`, per-run console logs, and matrix JSON). The values in this section are the repository-level record.

| Scenario | n | Stage ms (median) | Stage ms (range) | Pipeline ms (median) | Peak working set |
|---|---|---|---|---|---|
| Cold, fresh process, isolated engine cache | 5 | 5483.2 | 4748.0–5610.5 | 7515.6 | 416.9 MB (401.3–447.1) |
| Cold, fresh process, compatible (warm) cache | 5 | 5473.6 | 4604.0–9304.1 (rep 2 contention outlier) | — | — |
| Warm host | 32 | p50 4328.0 (mean 4508.9) | 2190.0–8785.4; p90 7255.7, p99 8513.4 | p50 5157.7, p90 8341.5 | min 461.4 / p50 512.9 / max 648.2 MB |
| Silence fixture, cold | 1 | 3227.5 | — | 5702.5 | 249.9 MB |

Warm-host CPU%: p50 15.7, p95 23.8. GPU memory: harness `gpuBytes` = Skipped and `availableVramMb` = Unavailable on the portable `net10.0` TFM (the host registers `UnavailableAvailableVramReader` because no platform VRAM source is available); adapter-wide `nvidia-smi` snapshots only (11388 MiB after cold / 9433 after warm / 2021 after TTS of 12227 — not attributable to the process). Provider matrix on `audio-preparation` is rejected by the harness ("Provider pin requires a runtime-backed focused stage"); it needs model-backed stages (ASR/TTS) with staged models.

## Reference machine (fill before claiming budgets)

| Field | Value |
|---|---|
| OS | Windows 11 Pro 10.0.26200 x64 |
| CPU | AMD Ryzen 7 5700X3D (8 cores / 16 threads) |
| RAM | 68,613,902,336 bytes (~63 GiB) |
| GPU / EP | NVIDIA GeForce RTX 5070, 12,227 MiB, driver 617.14; `NvTensorRTRTXExecutionProvider` via EP ABI plugin 0.4.2/cu13 (`trackdub providers trt-rtx status` → `ready:true`, `isRegisteredWithOrt:true`); DirectML remains the fallback path |
| Trackdub commit | Core `90cdcd75` for the test/provider evidence below; desktop startup rows measured on gated `42546ad` (`1.0.0+42546ad` Release build) with core pin `67fdc36e` |
| TFM exercised | `net10.0-windows10.0.19041.0` (Windows) / `net10.0` (portable) |

## Startup (cold, ms)

Measured from process launch to first interactive shell frame (no project open).

| Scenario | Target (draft) | Measured | Notes |
|---|---:|---:|---|
| Avalonia shell cold start | TBD | median 19,305.8; n=5; range 10,145.3–28,826.1 | App-reported launch→`firstVisibleUi` (BenchmarkEvidenceReport `desktop-startup`), Release `net10.0-windows10.0.19041.0`; isolated `TRACKDUB_DATA_ROOT`/`TRACKDUB_CACHE_ROOT` wiped per rep (cold app state; OS page cache not cleared) |
| Avalonia shell warm start (cache-warm roots) | TBD | median 9,207.5; n=32; range 4,207.3–26,990.2; p95 21,138.7 | Same harness, roots persist across reps; warm tail (largest 5): 18,992.9 / 19,006.7 / 19,053.5 / 21,138.7 / 26,990.2; run-order last 5: 12,815.4 / 12,994.3 / 15,788.1 / 7,422.6 / 6,590.5 |
| Shell + empty project open | TBD | launch → interactive shell median 5,339.7 ms (n=10; range 4,901.9–6,579.6); select the recent media-only project → visible in titlebar median 1,430.5 ms (n=10; range 1,279.8–1,618.5); → last display-state apply median 756.6 ms (692.3–973.6) | Debug config + `Optimize=true`, **not** the Release build of the rows above; project folder is a fresh copy per rep, so SQLite migrate/open is paid every rep. Detail and caveats in [the 2026-10-10 section](#desktop-project-open-model-manager-and-memory-2026-10-10) |
| Model Manager gate (bundled ONNX) | TBD | AI Models click → Settings window median 1,869.8 ms (n=10; range 991.4–2,023.1); → per-model readiness text rendered median 2,353.2 ms (2,193.7–2,485.5), empty model cache. Populated cache (n=5): 1,990.2 / 3,040.8 ms | Interpreted as opening Settings → Local Models, which is what the AI Models button does. Readiness is read per row ("Not downloaded", downloaded…), never collapsed; this is not a bundled-model integrity verify (that is the separate Verify all button). Same Debug-config caveat |

Raw per-rep samples are host-local on the reference machine and not committed (`c7-evidence/{cold,warm,smoke}.csv`, harness `Measure-DesktopStartup.ps1` in the same directory); per-run app reports are under `cold|warm/data/benchmark-reports/` in that directory. Variance is high; the warm distribution has not converged by rep 32, so treat medians as provisional.

### Desktop project open, Model Manager and memory (2026-10-10)

Measured on the reference machine (RTX 5070, Ryzen 7 5700X3D, 63 GB), core `eaeed1d9` (= gated pin), desktop built from gated `b1f6f755`.

**Build configuration, read first.** A Release desktop build refuses to start without a signed `trackdub.revocation.json`, which is deliberately not checked in (`docs/licensing/trust-ring-schema.md` in the gated repo) and was not available here. These rows therefore use `dotnet build -c Debug -p:Optimize=true -f net10.0-windows10.0.19041.0`, which selects the development trust ring and also attaches Avalonia developer tools (`#if DEBUG`). They are **not** like-for-like with the Release startup rows above. For reference, the idle shell in this same configuration: cold `firstVisibleUi` median 8,462.3 ms (n=5; 7,339.9–13,060.2), warm median 6,077.2 ms (n=10; 4,607.9–11,688.2), working set about 289 MB.

**Host state.** Not quiet. Host CPU load was 59–100% in the samples taken before each set (Docker, several node processes, an IDE agent). OS file caches were not cleared. The post-translation set ran in a noisier stretch (launch → interactive shell median 8,440.2 ms against 5,339.7 ms for media-only), so treat medians as provisional.

**Method.** A host-local UI Automation harness (`Measure-DesktopProject.ps1`, raw CSVs and run logs under `c7-evidence/2026-10-10-step22-ui/`, not committed) drives the real app. Every rep uses fresh isolated `TRACKDUB_DATA_ROOT` / `TRACKDUB_CACHE_ROOT`, a saved settings template with onboarding already dismissed, and a fresh copy of the project folder listed as the only recent project, so SQLite migrate/open is paid every rep. It waits for the `RecentProjectsCombo`, settles 3 s, selects the project, and records (a) when the titlebar leaves *No project*, (b) app-log milestones read after the app exits (the logger buffers, so they cannot be read live; the clock is the app's own), (c) working set, private bytes and GPU-process memory 10 s after the project is visible, then (d) clicks **AI Models** and times the Settings window and its per-model readiness text. All three projects come from the 8 s `short.mp4` fixture and have **one segment**: `trackdub project create` (media-only) and the `baseline` projects from `controlled-matrix --stages asr` and `--stages translation`.

| Project state | n | select → titlebar (ms) | select → last display-state apply (ms) | select → `Snap.Preview.FirstFrame` (ms) | working set (MB) |
|---|---|---|---|---|---|
| media-only (spine only) | 10 | 1,430.5 (1,279.8–1,618.5) | 756.6 (692.3–973.6) | 1,241.8 (1,163.4–1,457.3) | 427.1 (420.6–439.2) |
| post-ASR | 9 of 10 | 1,559.5 (1,363.2–2,090.6) | 770.9 (734.1–915.5) | 1,317.7 (1,200.1–1,614.6) | 429.8 (419.0–456.7) |
| post-translation | 9 of 10 | 2,700.2 (2,008.6–3,115.9) | 1,457.2 (884.7–1,594.3) | 2,284.3 (1,677.2–2,671.2) | 432.4 (427.4–445.1) |
| post-translation, populated model cache | 5 | 1,991.8 (1,851.4–23,452.5) | 863.2 (815.7–22,485.4) | 1,675.6 (1,595.3–23,161.1) | 435.0 (429.1–652.4) |

Medians with ranges in parentheses.

| Model Manager (AI Models click) | n | → Settings window (ms) | → per-model readiness text (ms) |
|---|---|---|---|
| media-only set, empty model cache | 10 | 1,869.8 (991.4–2,023.1) | 2,353.2 (2,193.7–2,485.5) |
| post-ASR set, empty model cache | 9 | 1,022.8 (925.5–1,850.4) | 2,303.7 (2,107.8–3,623.9) |
| post-translation set, empty model cache | 9 | 2,928.5 (1,925.6–3,865.7) | 3,478.5 (2,372.8–4,525.3) |
| post-translation set, populated model cache | 5 | 1,990.2 (1,528.2–2,430.5) | 3,040.8 (2,818.5–3,604.7) |

- One rep in each of the post-ASR and post-translation sets failed in the harness (the recent-project item was not found in the dropdown), hence n=9. They are excluded, not imputed.
- The window time is bimodal (about 1.0 s in some reps, about 1.9 s in others) and the cause was not investigated.
- Projects that already carry normalized audio never log `Snap.Normalize.*` / `Snap.Stages.Ready`; only the media-only path does (median 2,332.9 ms, range 2,128.7–2,796.8). The last `ApplyDisplayState 'navigator'` line is the loaded mark common to all three states.
- **Populated model cache, first open:** with the app cache root pointed at the real `%LOCALAPPDATA%\Trackdub` (so the real `model-cache` is visible), rep 1 took 23,452.5 ms to show the project: the open was dispatched in 46.8 ms but project-state apply started 22,373.8 ms later, and the working set reached 652.4 MB. Reps 2–5 took 1.9–2.2 s. The cause was not diagnosed (the app's console output shows ONNX Runtime session-creation warnings during that window); the n=5 median hides it.
- Working set is flat across states (about 430 MB, against 289 MB idle) because these projects have one segment. It says nothing about a multi-hundred-segment project.

### Example row format (illustrative only — not measured)

Replace these example values after a reference-machine run. They exist only to show how completed tables should read.

| Scenario | Target (draft) | Measured (example) | Notes |
|---|---:|---:|---|
| Avalonia shell cold start | 500 | 480 | example only |
| Idle shell, no media | 350 MB | 320 MB | example only |
| Audio mix export (5 min source) | RTF ≤ 1.0 | 0.85 | example only |

## Working set (MB)

Private bytes / working set after steady state (5 min idle, no pipeline run).

| Scenario | Target (draft) | Measured | Notes |
|---|---:|---:|---|
| Idle shell, no media | TBD | working set median 281.8 MB (warm, n=32, range 268.5–287.5); private bytes median 263.0 MB; GPU process memory median 91 MB (range 91.0–101.4) | Sampled ~8 s after `firstVisibleUi`, not the 5-min-idle methodology above; post-start settle sample, real but shallow |
| Project open, transcript loaded | TBD | working set median 429.8 MB (n=9; range 419.0–456.7); private bytes median 473.4 MB; GPU process 248.4 MB. Idle shell, same build config: working set 289.2 MB median (cold, n=5; range 285.8–292.2) | Post-ASR project, **one segment from an 8 s clip**, sampled 10 s after the project became visible. Not a "typical editor session": a multi-hundred-segment project was not measured. Debug config + `Optimize=true` |
| Post-ASR + translation (no TTS) | TBD | desktop with a post-translation project: working set median 432.4 MB (n=9; range 427.4–445.1). Benchmark-host process (`net10.0`, fresh process, vad → diarization → asr → translation): peak working set 12,269 MB; vad → diarization → asr alone: 6,637 MB | Two different things: the desktop figure is the app showing artifacts on disk; the benchmark-host figure is the engines resident in one process (Qwen3-ASR 0.6B and MADLAD400, both on CPU). Peak sampling cadence was dilated (longest tick gap 216–482 ms at a 25 ms cadence), so transient peaks may be missed |

## Export throughput

| Export profile | Media duration | Wall time | Real-time factor | Measured |
|---|---:|---:|---:|---|
| Audio mix (default) | 8.0 s | median 3,768.1 ms (n=10; range 3,588.9–7,027.0) | 0.47 | measured 2026-10-10, core `eaeed1d9`; see method below |
| Video mux (if applicable) | 8.0 s | not separable | not separable | The export stage emits `dub.wav`, `dubbed.mp4` and `dubbed.srt` in one stage run; the product records one duration for all of it, so the mix and the mux are not timed separately |

**Method:** note FFmpeg/libmpv path, segment count, and whether `MatchOriginalLoudness` was enabled.

**2026-10-10 run:** `trackdub run-stage --stage export` ×10 (CLI, Release `net10.0`) against one project built from the baseline `short.mp4` (sha256 `c4640c3f…95bd85`, 8.000 s, **one segment**): default `mp4` container, `ffmpeg` on PATH, `MatchOriginalLoudness=False`, target −14 LUFS, `ApplyTimbrePolish=True`, `VideoEncoder=auto`, no burned-in subtitles. Wall time is the stage's own `StartedAtUtc`→`CompletedAtUtc` from the project's `StageRuns`; the CLI process wall time (including .NET and session start-up) was median 7,469.2 ms (7,215.0–13,667.2). RTF is stage time divided by media duration. With an 8 s clip fixed costs dominate, so **0.47 does not predict throughput for long media**; no long-form export was measured. One rep (7,027.0 ms) is a host-contention outlier.

**Export did not run with default timing on this fixture.** The default dubbed take (Qwen3-TTS 0.6B, CPU) for the translated `en`→`fr` text extended 2.0 s and 3.7 s past the 8 s source in the two matrix runs, and the export stage refuses that ("Dubbed take extends … past the end of the source audio … Stretch the take to fit, trim it, or raise the auto-stretch limit"), so `controlled-matrix --stages export` ends `STAGE_FAILED` here (twice, including once with `--mode warm-host`). The export timings above were taken after re-running TTS with `--tts-auto-stretch-max-overrun 1`. A Kokoro attempt did not help: the `--model tts=kokoro-onnx` pin was not applied to the prerequisite TTS stage in the matrix run (it still used Qwen3-TTS), and a direct `run-stage --stage tts --model kokoro-onnx` take overran by 5.3 s.

## SQLite query plans

Hot paths audited in `tests/Trackdub.Infrastructure.Tests/SqliteExplainQueryPlanTests.cs`:

| Query | Table / index expectation | CI audit |
|---|---|---|
| Glossary by project + language pair | `glossary_entries` / `ix_glossary_entries_project_language` | EXPLAIN asserts indexed `SEARCH` |
| Stage runs by project | `StageRuns` / `ix_stage_runs_project_id` | EXPLAIN asserts indexed `SEARCH` |
| Transcript segments by revision | `transcript_segments` / `ix_transcript_segments_revision_id` | EXPLAIN asserts indexed `SEARCH` |
| Glossary empty project (0 rows) | same glossary index | EXPLAIN still `SEARCH`; no rows seeded |
| Stage runs empty project (0 rows) | same stage-run index | EXPLAIN still `SEARCH`; no rows seeded |
| Transcript segments empty revision | same segment index | revision saved with 0 segments |
| Glossary large fixture (~400 rows) | same glossary index | indexed `SEARCH` under volume |
| Stage runs large fixture (~250 rows) | same stage-run index | indexed `SEARCH` under volume |
| Transcript segments large fixture (~1.2k rows) | same segment index | indexed `SEARCH` under volume |

**Notes:**

- Audit uses `EXPLAIN QUERY PLAN` on a migrated project DB with representative seed rows.
- EXPLAIN verifies indexed `SEARCH` plan shape; it does not assert wall-clock latency. Stale stats or poor selectivity can still make an indexed query slow — see follow-up item 6.
- Empty-table cases still assert indexed `SEARCH` for the hot-path predicates used in production queries.
- Full project-scale soak (10k+ segments) remains a follow-up measurement pass, not a CI budget gate yet.

## Profiler / benchmark history

| Source | Location | Status |
|---|---|---|
| DubBench / `Trackdub.Benchmarks` harness | `src/Trackdub.Benchmarks` | Initial controlled local samples and raw report IDs in [benchmark evidence](../development/benchmark-evidence.md); comparison medians pending compatible repeats |
| Inference session pool tests | `tests/` (session pooling) | present in repo; link results in follow-up |
| User benchmark SQLite (`BenchmarkRuns` table) | per-user DB | wired on `main` via M19; link results in follow-up |
| Hardware profiler history recorder | `src/Trackdub.Composition/HardwareProfiler` | present on `main`; capture history path in follow-up |

Record commit hash, model manifest IDs, and EP selection policy (`WindowsMlExecutionDevicePolicy`) with every benchmark run.

### TensorRT RTX EP ABI plugin (Windows NVIDIA)

| Field | Value |
|---|---|
| Model id | `onnx-community/whisper-tiny`, `whisper-base`, `whisper-small` (ONNX path, `providers trt-rtx smoke` → PASS each); staged TRT-RTX smokes also pass for `whisper-{tiny,base,small,medium,large-v3}` + `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` via `RequiresTrtRtxStagingFact` tests (6/6 on 2026-10-06). Whisper **GenAI** variants are excluded by planner policy (host-process native stack overflow guard), so smoke reports FAIL by design, not a crash |
| Plugin version | `0.4.2/cu13` (TRT-RTX 1.6.1) |
| Command | `trackdub providers trt-rtx status` → `ready:true`; `trackdub providers trt-rtx smoke` → PASS on ONNX whisper models |
| Headless probe | `trackdub providers trt-rtx status` → `{"ready":true,"isRegisteredWithOrt":true,"isHardwareEligible":true}` |
| Wall time (ms) | ~70–290 s per whisper ONNX model in `smoke`, dominated by first-run TRT engine compile (log span 18:04:32→18:09:46 UTC for 3 models); inference itself is sub-second once engines are cached |
| Actual EP reported | `NvTensorRTRTXExecutionProvider` (engine logs show `tensorrt_rtx_execution_provider` node compilation; `providers status` reports `isRegisteredWithOrt:true`) |
| Commit SHA | `90cdcd75` |
| Plugin dir | `%LOCALAPPDATA%\Trackdub\Providers\trt-rtx\0.4.2\cu13\win-x64` or `TRACKDUB_TRT_RTX_EP_DIR` |

### Real-model parity and staged TRT-RTX smokes at core `eaeed1d9` (2026-10-10)

Run on the reference machine at the current core `main` tip (clean worktree), `net10.0-windows10.0.19041.0`, Release, with `TRACKDUB_TRT_RTX_SMOKE=1`, `TRACKDUB_OPUS_FIXTURE_ROOT=<model-cache>\onnx-community\opus-mt-en-es\onnx` and `TRACKDUB_MADLAD_FIXTURE_ROOT=<model-cache>\google\madlad400-3b-mt`. The gitignored `build/*-trtrtx-validated*` staging directories come from the Olive validation runs of 2026-09-22/23 (`build/sortformer-4spk-trtrtx-validation.json` and `build/whisper-onnx-trtrtx-validation.json`, the latter holding only the last size run, large-v3, both read `"pass": true`; the per-size Whisper evidence is the staged tests below). Olive was **not** re-run. **10 executed, 10 passed, 0 skipped**; raw TRX and console log are host-local under `c7-evidence/2026-10-10-step23-eaeed1d9/`.

| Test | What it asserts | Result | Duration |
|---|---|---|---|
| `OpusMtTranslationEngine_UsesFixtureModelWhenProvided` | `en`→`es` through `opus-mt-en-es` returns non-empty text on `cpu` | pass | 4 m 43.6 s |
| `MadladTranslationEngine_UsesFixtureModelWhenProvided` | MADLAD400 int8 returns non-empty text on `cpu` | pass | 1 m 05.7 s |
| `MadladTranslationEngine_StreamParity_MatchesBatchPerSegment` | streaming and batch translation agree per segment (2 segments) | pass | 3 m 05.4 s |
| `MadladTokenizerDecoder_EncodeSourceText_TerminatesWithEndOfSentence` | source ids end in a single `</s>` | pass | 1.4 s |
| `SortFormerDiarizationEngineTests.DiarizeAsync_with_trtrtx_staged_model_selects_tensorrt_rtx_provider` | staged fp16 SortFormer selects the TensorRT RTX provider | pass | 1 m 25.7 s |
| `WhisperOnnxTrtRtx_{Tiny,Base,Small,Medium,LargeV3}Model_SessionLoadsAndTranscribesSilence` | each staged Whisper loads on TRT-RTX and transcribes silence | pass ×5 | tiny 51.1 s, base 48.3 s, small 57.6 s, medium 3 m 11.0 s, large-v3 4 m 52.3 s |

Scope limits: the Opus and MADLAD tests are fixture smokes plus streaming-vs-batch equivalence; they do not score translation quality. Durations include TensorRT RTX engine builds and ran on a busy host. The staging directories encode a model-cache state at validation time; if a cached model changes, the validation scripts must be run again. Earlier TRT-RTX smoke evidence (6/6 on 2026-10-06) was recorded at `90cdcd75`; this run replaces it with evidence at the pin.

## Avalonia UI / render budget (headless)

| Check | Test class | Evidence |
|---|---|---|
| Component layout + optional PNG | `ComponentScreenshotTests` | `CAPTURE_UI_SCREENSHOTS=1` → `.design/.../headless/components/` |
| Shell panel state | `ShellTests` | layout/state assertions (no PNG) |
| Main window / side panel / transport | `*LayoutTests` | bounds and alignment |
| Glossary panel chrome | `ComponentScreenshotTests.Glossary_panel_*` | expanded/collapsed layout |

Long waveform / timeline frame budget: measured 2026-10-10 as headless render cost, see below. This is CPU-side layout plus Skia software raster per frame, **not** GPU present latency or on-screen smoothness in the running app.

### Waveform frame cost, headless (2026-10-10)

`WaveformPeaksControl` (1600 × 140) on the Avalonia headless platform with the real Skia software renderer (`UseHeadlessDrawing = false`), `net10.0-windows10.0.19041.0`, Release, gated `b1f6f755` at core `eaeed1d9`. Each sample is one `CaptureRenderedFrame()` while `PositionSeconds` sweeps 90% of the duration; 30 warm-up and 300 measured frames per case. Peaks are synthetic and seeded; the bucket count follows the generator's rule (10 per second, capped at 12,000), and each case carries one segment lane and boundary per 4 s, capped at 1,500. Probe source and CSV are host-local under `c7-evidence/2026-10-10-step22-ui/`; the probe is a local, uncommitted test. Host CPU load was not quiet.

| Waveform | Buckets | Segments | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) |
|---|---|---|---|---|---|---|
| 60 s | 600 | 15 | 2.5–3.9 | 4.5–6.1 | 5.3–7.7 | 6.5–12.3 |
| 30 min | 12,000 | 450 | 7.1–7.6 | 11.0–12.0 | 11.6–12.9 | 13.6–19.4 |
| 2 h | 12,000 | 1,500 | 13.4–13.7 | 18.5–20.0 | 20.6–26.5 | 22.7–46.8 |

Each cell is the range over four zoom levels (2, 10, 50 and 200 px/s); zoom moved p50 by at most 1.4 ms in the 60 s case and 0.5 ms in the others. Against a 60 fps reference of 16.7 ms (a reference, not a project budget): the 60 s and 30 min cases stay under it at p99, the 2 h case with 1,500 segments exceeds it from p95 up. The 30 min and 2 h cases have the same bucket count, so the p50 growth from about 7.4 to about 13.5 ms tracks segment count (450 → 1,500) rather than peaks; that is consistent with per-frame cost growing with the number of segment lanes and boundaries, but viewport culling was not tested.

## Load snap budget (progressive import/open)

Progressive load emits structured snap events to `%LOCALAPPDATA%\Trackdub\trackdub.log` with the `Snap.` prefix (see `SnapBudgetLog` in `Trackdub.App.Avalonia`).

| Event | When |
|---|---|
| `Snap.Import.Start` | Import entry |
| `Snap.Spine.Ready` | After `CreateMediaSpineAsync` |
| `Snap.Shell.Bound` | After first shell apply with `reopenPlayback: false` |
| `Snap.Normalize.Start` / `Snap.Normalize.Ready` | Background normalize job |
| `Snap.Preview.Open.Start` / `Snap.Preview.FirstFrame` / `Snap.Preview.TimedOut` / `Snap.Preview.Failed` | Background preview job |
| `Snap.Stages.Ready` | After post-normalize pipeline row refresh |
| `Snap.LoadGeneration.Discarded` | Stale `(ProjectId, LoadGeneration)` callback dropped |

Draft targets (fill after a measured local import on reference hardware):

| Milestone | Target |
|---|---|
| Shell bound after spine | &lt; 500 ms from `Import.Start` |
| Preview first frame | &lt; 2 s from `Shell.Bound` |
| Normalize ready | background; must not block shell bind |

Example grep:

```powershell
Select-String -Path "$env:LOCALAPPDATA\Trackdub\trackdub.log" -Pattern 'Snap\.'
```

## Follow-up measurement pass (out of scope for PR4)

1. Fill reference machine table and commit measured startup / memory / export rows.
2. Run `Trackdub.Benchmarks` on reference hardware; attach output path or summary table here.
3. Add optional CI soft thresholds (warn-only) after two baseline runs agree.
4. Long-media UI frame timing with headless or controlled `dotnet-counters` session.
5. Project-scale SQLite EXPLAIN with 10k+ segment fixtures (beyond current ~1.2k CI audit).
6. Hot-path SQLite wall-clock micro-benchmarks on the same standardized fixtures (warn-only CI after two baseline runs agree).

## Commands (local)

```powershell
# SQLite EXPLAIN audit
dotnet test tests/Trackdub.Infrastructure.Tests --filter "FullyQualifiedName~Explain" -m:1

# UI component evidence (Windows TFM)
$env:CAPTURE_UI_SCREENSHOTS = "1"
dotnet test tests/Trackdub.UI.Tests -f net10.0-windows10.0.19041.0 --filter "FullyQualifiedName~ComponentScreenshot" -m:1

# Full solution build
dotnet build Trackdub.sln -m:1
```
