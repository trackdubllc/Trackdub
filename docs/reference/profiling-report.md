# Trackdub performance profiling report

> **Status:** DRAFT — scaffold (M20 PR4). Numbers marked *pending local run* are placeholders until measured on a reference machine.
> **Last updated:** 2026-06-13
> **Branch evidence:** `agent/cursor/m20-profiling-report`

## Measurement methodology (fill before claiming budgets)

Use the same procedure on every run so rows in this report stay comparable.

| Step | Tool / command | Record in report |
|---|---|---|
| Cold startup | `Stopwatch` from `Main` entry to first interactive shell frame, or ETW/`dotnet-trace` | ms, TFM, commit SHA |
| Working set | Task Manager **Private working set** or `dotnet-counters monitor --counters System.Runtime` | MB after 5 min idle |
| Export throughput | Wall clock around export command; note FFmpeg profile and segment count | duration, real-time factor |
| SQLite plans | `dotnet test tests/Trackdub.Infrastructure.Tests --filter FullyQualifiedName~Explain` | pass/fail + index names |
| UI layout | `Trackdub.UI.Tests` layout facts; PNG only when `CAPTURE_UI_SCREENSHOTS=1` | test name + optional PNG path |
| Inference / export bench | `dotnet run --project src/Trackdub.Benchmarks -- --help` then targeted scenario | log path, model manifest IDs, EP policy |

**Rules:** never collapse provider registered, model downloaded, stage ran, and stage succeeded. Label every number as *measured on reference machine* or *pending local run*. Do not copy example rows below into release notes as real data.

## Reference machine (fill before claiming budgets)

| Field | Value |
|---|---|
| OS | *pending local run* |
| CPU | *pending local run* |
| RAM | *pending local run* |
| GPU / EP | *pending local run* (Windows ML policy, DirectML fallback, etc.) |
| Trackdub commit | *pending local run* |
| TFM exercised | `net10.0-windows10.0.19041.0` (Windows) / `net10.0` (portable) |

## Startup (cold, ms)

Measured from process launch to first interactive shell frame (no project open).

| Scenario | Target (draft) | Measured | Notes |
|---|---:|---:|---|
| Avalonia shell cold start | TBD | *pending local run* | `dotnet run --project src/Trackdub.App.Avalonia -f net10.0-windows10.0.19041.0` |
| Shell + empty project open | TBD | *pending local run* | Includes SQLite migrate/open |
| Model Manager gate (bundled ONNX) | TBD | *pending local run* | Separate from shell; do not collapse readiness states |

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
| Idle shell, no media | TBD | *pending local run* | Task Manager or `dotnet-counters` |
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
| DubBench / `Trackdub.Benchmarks` harness | `src/Trackdub.Benchmarks` | *pending local run* — capture baseline JSON or log excerpt |
| Inference session pool tests | `tests/` (session pooling) | present in repo; link results in follow-up |
| User benchmark SQLite (`BenchmarkRuns` table) | per-user DB | wired on `main` via M19; link results in follow-up |
| Hardware profiler history recorder | `src/Trackdub.Composition/HardwareProfiler` | present on `main`; capture history path in follow-up |

Record commit hash, model manifest IDs, and EP selection policy (`WindowsMlExecutionDevicePolicy`) with every benchmark run.

### TensorRT RTX EP ABI plugin (Windows NVIDIA)

| Field | Value |
|---|---|
| Model id | *pending local run* (`onnx-community/silero-vad` suggested) |
| Plugin version | `0.3.0/cu12` |
| Command | `Trackdub.Benchmarks --provider trt-rtx --runs 1 --format console` |
| Headless probe | `trackdub providers trt-rtx status` |
| Wall time (ms) | *pending local run* |
| Actual EP reported | *pending local run* (`NvTensorRTRTXExecutionProvider`) |
| Commit SHA | *pending local run* |
| Plugin dir | `%LOCALAPPDATA%\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64` or `TRACKDUB_TRT_RTX_EP_DIR` |

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
