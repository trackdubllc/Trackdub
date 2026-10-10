# Trackdub performance profiling report

> **Status:** MIXED — controlled dubbing-pipeline samples are recorded below; reference-machine, startup, idle working-set, TRT-RTX EP rows measured 2026-10-06, and audio-preparation matrix rows (cold ×5 isolated + cold ×5 compatible + warm ×32 + silence) measured 2026-10-07; project-open/model-manager startup, steady-state memory, export, and waveform rows marked *pending local run* remain unmeasured.
> **Last updated:** 2026-10-07
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
| Shell + empty project open | TBD | *pending local run* | Includes SQLite migrate/open |
| Model Manager gate (bundled ONNX) | TBD | *pending local run* | Separate from shell; do not collapse readiness states |

Raw per-rep samples are host-local on the reference machine and not committed (`c7-evidence/{cold,warm,smoke}.csv`, harness `Measure-DesktopStartup.ps1` in the same directory); per-run app reports are under `cold|warm/data/benchmark-reports/` in that directory. Variance is high; the warm distribution has not converged by rep 32, so treat medians as provisional.

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
| Project open, transcript loaded | TBD | *pending local run* | Typical editor session |
| Post-ASR + translation (no TTS) | TBD | *pending local run* | Pipeline artifacts on disk; memory in-process |

## Export throughput

| Export profile | Media duration | Wall time | Real-time factor | Measured |
|---|---:|---:|---:|---|
| Audio mix (default) | *pending local run* | *pending local run* | *pending local run* | *pending local run* |
| Video mux (if applicable) | *pending local run* | *pending local run* | *pending local run* | *pending local run* |

**Method:** note FFmpeg/libmpv path, segment count, and whether `MatchOriginalLoudness` was enabled.

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

## Avalonia UI / render budget (headless)

| Check | Test class | Evidence |
|---|---|---|
| Component layout + optional PNG | `ComponentScreenshotTests` | `CAPTURE_UI_SCREENSHOTS=1` → `.design/.../headless/components/` |
| Shell panel state | `ShellTests` | layout/state assertions (no PNG) |
| Main window / side panel / transport | `*LayoutTests` | bounds and alignment |
| Glossary panel chrome | `ComponentScreenshotTests.Glossary_panel_*` | expanded/collapsed layout |

Long waveform / timeline frame budget: *pending local run* (needs media fixture + scrub profile).

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
