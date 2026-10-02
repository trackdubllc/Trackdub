# Benchmark Regression Triage

Establishes, with measured numbers, why a benchmark moved and whether to accept, fix, or revert the change. The governing rule is absolute: **measured numbers only, never estimates.** If a number cannot be reproduced on demand, it is recorded as `NOT VERIFIED` and carries no weight in the verdict. This workflow is explicitly invoked — benchmarks are not part of the Release `-warnaserror` CI gate and are not run on pull-request CI by policy.

## When to Use This Workflow

Use when:
- A benchmark number moved beyond the tolerance the human considers meaningful.
- A stage's latency, throughput, or memory regressed after a change.
- Someone claims an improvement that has no artifact behind it.
- A/B comparison is needed between two commits, providers, or models.
- A saved-commit benchmark comparison is requested.

Do NOT use for:
- Correctness failures. This workflow measures time and size, not pass/fail.
- General PR hygiene → `pr-ready-loop.md`.
- New stage implementation → `new-inference-stage.md`.
- Native dependency pin bumps → `/pin`.

## Inputs

| Input | Required | Description | Default |
|---|---|---|---|
| Baseline reference | yes | Commit, run id, or report the comparison is against | none — **no baseline means stop** |
| Candidate under test | yes | Current commit or working tree | working tree |
| Scope | yes | Which stage(s) or project(s) to measure | the changed stage |
| Hypothesis | no | Suspected cause, to be confirmed or killed by measurement | none |
| Tolerance | yes | Human-declared threshold that counts as a regression | none — human states it |
| Machine | yes | Physical host used for all measurements | must match the baseline host |

## Preconditions

- A baseline exists and is identifiable. If not, stop and say so.
- Baseline and candidate were measured on the **same machine**. Cross-host comparison is invalid; record `NOT VERIFIED` instead.
- PowerShell 7 is available (verified: `pwsh` 7.6.6) — the harness scripts are `.ps1`.
- Build output is current: `dotnet build Trackdub.slnx -m:1`.
- Reports land in `$env:LOCALAPPDATA\Trackdub\benchmark-reports` (verify the directory exists after a run).

## Execution Stages

### Stage 1 - Intake and Baseline Confirmation

- **Goal**: Establish what regressed and against what, or stop.
- **Actor**: subagent `benchmark-perf`
- **Context to load**: `.opencode/context/domain/inference-stack.md`
- **Actions**:
  1. State the observed number, where it came from, and when it was observed.
  2. Identify the baseline: commit sha, run id, or report file. **If no baseline can be identified, STOP.** An unanchored "it got slower" is not triageable.
  3. Confirm the human's tolerance threshold. The agent does not invent one.
  4. List every change between baseline and candidate.
- **Exit criteria**: A specific baseline artifact and a specific candidate are both named.
- **Failure handling**: No baseline ⇒ stop and report the blocker. Do not substitute a guess, a doc number, or a remembered value.

### Stage 2 - Environment Capture

- **Goal**: Freeze every environmental variable that can move a number.
- **Actor**: subagent `benchmark-perf`
- **Context to load**: `.opencode/context/domain/inference-stack.md`, `src/Trackdub.Benchmarks.Micro/README.md`
- **Actions**:
  1. Record OS and build. Verify the TFM actually used — the harness scripts target `net10.0-windows10.0.19041.0`, while `controlled-matrix` is documented for `-f net10.0`.
  2. Record GPU model and driver version. Verify; do not assume.
  3. Record the ONNX Runtime provider actually selected. The report exposes `stages[].actualProvider` and `stages[].actualModel` — read them from the artifact rather than trusting the requested values.
  4. Record model manifest and checksums: `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`.
  5. Record build configuration (Release for any BDN work) and whether the engine cache was reused (`--reuse-engine-cache`).
  6. Record power/thermal state. Thermal throttling is a first-class confounder on long GPU runs.
- **Exit criteria**: Environment captured and recorded alongside every number that follows.
- **Failure handling**: Environment differs from baseline ⇒ `NOT VERIFIED`. Report the difference and stop the comparison.

### Stage 3 - Reproduce on Current

- **Goal**: Re-measure the candidate from a clean state.
- **Actor**: subagent `benchmark-perf`
- **Context to load**: `.opencode/context/processes/adding-pipeline-stage.md`
- **Actions**:
  1. `dotnet build Trackdub.slnx -m:1`
  2. Confirm the exact command surface before running: `dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- --help`.
  3. Run the scoped measurement. For per-stage isolation use `tools/bench-per-stage.ps1`; for an A/B verdict use `tools/bench-smoke-verdict-ab.ps1`.
  4. Read the report from `$env:LOCALAPPDATA\Trackdub\benchmark-reports`, filtering `kind`/`Kind` equal to `Benchmark`. The harness selects these by modification time — confirm the file you read is the file the run produced.
  5. Record `status`, `timingsMilliseconds.preflight`, `.pipeline`, `.total`, `stages[].durationMilliseconds`.
  6. Repeat the measurement at least twice. Single-shot numbers are noise, not data.
  7. If the number will not reproduce across repeats, mark it `NOT VERIFIED`.
- **Exit criteria**: At least two consistent measurements with matching environment capture.
- **Failure handling**: Non-reproducing or failing run ⇒ `NOT VERIFIED`; route structural failures to `core-diagnostics`.

### Stage 4 - Diff Against Baseline

- **Goal**: Quantify the delta precisely.
- **Actor**: subagent `benchmark-perf`
- **Context to load**: `.opencode/context/templates/evidence-report.md`
- **Actions**:
  1. Compare like-for-like only: same stage, same provider, same model, same mode, same fixture.
  2. Compute absolute and percentage deltas per stage. Separate `preflight` from `pipeline` — a preflight regression is a readiness/loading problem, not a compute problem.
  3. State whether the delta exceeds the human's tolerance.
  4. For saved-commit comparisons use `scripts/ci/run_benchmarkdotnet_baseline.py`, comparing like-for-like benchmark names.
  5. For BenchmarkDotNet micro work, list first with `dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- --list flat`, then run in Release with deterministic inputs and `GlobalSetup`. Keep model, tokenizer, file, and session initialization **outside** measured methods.
- **Exit criteria**: A signed delta table with an explicit over/under-tolerance verdict per stage.
- **Failure handling**: Numbers not comparable across stage, provider, model, or mode ⇒ discard; re-measure like-for-like.

### Stage 5 - Attribution

- **Goal**: Name the cause from evidence, not from plausibility.
- **Actor**: subagent `benchmark-perf`; structural findings to `core-diagnostics`
- **Context to load**: `.opencode/context/domain/inference-stack.md`, `docs/benchmarks/benchmarkdotnet.md`
- **Actions**:
  1. Walk the decision tree below. Stop at the first branch the evidence supports.
  2. Confirm `actualProvider`/`actualModel` from the report. A silent provider fallback is a frequent root cause and is invisible unless read.
  3. Do not revert changes or prepare a worktree as the read-only benchmark agent. For BenchmarkDotNet, use `scripts/ci/run_benchmarkdotnet_baseline.py` when a saved-commit comparison applies. For controlled runs, ask an authorized actor to prepare the baseline worktree; otherwise report `ATTRIBUTION NOT VERIFIED`.
  4. State the attribution as a hypothesis with the measurement that supports it, plus the measurement that would falsify it.
- **Exit criteria**: One primary attribution with supporting evidence.
- **Failure handling**: Evidence supports no single cause ⇒ report all live hypotheses; do not collapse to the most likely-sounding one.

### Stage 6 - Isolation via Per-Stage Measurement

- **Goal**: Confine the regression to one stage.
- **Actor**: subagent `benchmark-perf`
- **Context to load**: `.opencode/context/domain/inference-stack.md`
- **Actions**:
  1. Run `tools/bench-per-stage.ps1`. It exercises `vad`, `asr`, `translation`, and `tts` (with `cosyvoice-300m` and `chatterbox-turbo-onnx`) across `fresh-process` and `warm-host` modes on the `Cpu` provider, and writes `per-stage-matrix.csv` under `$env:LOCALAPPDATA\Trackdub\benchmark-smoke-verdict\per-stage`.
  2. The script defaults to fixture `$env:LOCALAPPDATA\Trackdub\benchmark-fixtures\baseline-v1\short.mp4`. Verify it exists before running.
  3. Compare `fresh-process` against `warm-host`. A regression that appears only in `warm-host` points at caching or state; one in both points at compute.
  4. For end-to-end evidence prefer `controlled-matrix`, which preserves each stage's evidence separately:
     `dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- controlled-matrix <fixture> --output <dir>`
  5. `Trackdub.Benchmarks` is the source of truth for controlled end-to-end evidence and `BenchmarkEvidenceReport`. Do not merge or replace it with BenchmarkDotNet artifacts.
- **Exit criteria**: Regression localized to a named stage and mode, or proven not stage-local.
- **Failure handling**: `NO-REPORT` from the harness means no measurement was produced. That is not a fast result — it is `NOT VERIFIED`.

### Stage 7 - Decide

- **Goal**: Convert evidence into accept, fix, or revert.
- **Actor**: `trackdub-orchestrator` decides; `benchmark-perf` recommends
- **Context to load**: `.opencode/context/templates/evidence-report.md`
- **Actions**:
  1. **Accept** — delta is inside tolerance, or is attributable to host noise proven by revert-and-rerun. Record the number and the tolerance.
  2. **Fix** — attribution names a cause in the change and a bounded fix exists. Fix, then re-measure the same way. Never fix and re-measure with different parameters.
  3. **Revert** — attribution names the change as the cause and no bounded fix exists, or the regression exceeds tolerance with no understood cause. Revert, then re-measure to confirm the baseline is restored.
  4. Any fix or revert routes through `pr-ready-loop.md` for the Release gate. **Benchmarks never replace the correctness gate**, and threshold results stay separate from correctness tests.
- **Exit criteria**: A recorded decision with the numbers that justify it.
- **Failure handling**: Cannot decide ⇒ escalate to human with the measurement table and the live hypotheses.

### Stage 8 - Record

- **Goal**: Leave a durable record that the next reader can trust.
- **Actor**: subagent `benchmark-perf`
- **Context to load**: `.opencode/context/templates/evidence-report.md`
- **Actions**:
  1. Record environment, commands, report paths, per-stage deltas, attribution, decision, and every `NOT VERIFIED` item.
  2. Note explicitly whether BDN or the controlled harness was used. They are not interchangeable evidence.
  3. Note that BDN does not run on PR CI; CPU runs are manual/nightly and real-model ONNX or saved-commit comparisons are opt-in via `.github/workflows/benchmark-dotnet.yml`.
  4. Never record an estimate as a measurement. Unverified entries carry no weight in a future decision.
- **Exit criteria**: Record complete enough for another engineer to reproduce without re-deriving context.
- **Failure handling**: Missing environment capture invalidates the whole record; re-measure rather than backfill.

## Decision Points

| Condition | Decision |
|---|---|
| No baseline identifiable | Stop. Report the blocker |
| Different machine or environment from baseline | `NOT VERIFIED`; discard the comparison |
| Numbers will not reproduce across repeats | `NOT VERIFIED`; no verdict |
| Harness reports `NO-REPORT` | No measurement exists. Not a result |
| `actualProvider` differs from requested | Silent fallback; re-measure before any verdict |
| Delta inside tolerance | Accept, with numbers recorded |
| Delta in `fresh-process` and `warm-host` | Compute path |
| Delta only in `warm-host` | Caching or retained state |
| Delta in `preflight`, not `pipeline` | Model load / readiness path, not compute |
| Attribution survives reverting the change | Host noise, not the change |
| Attribution disappears on revert | The change is the cause |
| Fix exists and is bounded | Fix, re-measure identically |
| No bounded fix | Revert and confirm the baseline is restored |

## Gates

- **G0 — Baseline gate** (end Stage 1): a specific baseline artifact is named. Blocks everything.
- **G1 — Environment gate** (end Stage 2): candidate environment matches baseline, else `NOT VERIFIED`.
- **G2 — Reproduction gate** (end Stage 3): at least two consistent measurements. A single run never passes.
- **G3 — Attribution gate** (end Stage 5): one evidence-backed cause, or all live hypotheses reported.
- **G4 — Decision gate** (end Stage 7): accept / fix / revert, each backed by recorded numbers.

## Failure Modes

| Symptom | Likely cause | Recovery |
|---|---|---|
| No baseline exists | Never measured | Stop; establish a baseline first |
| `NO-REPORT` from the harness | Run produced no report | Treat as `NOT VERIFIED`; check `$env:LOCALAPPDATA\Trackdub\benchmark-reports` and the exit code |
| Provider differs from what was requested | Silent fallback | Read `stages[].actualProvider`; re-measure on the intended provider |
| Timing moves with no code change | Thermal/power/noise | Revert-and-rerun on the same host; only claim noise with that evidence |
| One fast run followed by a slow one | Cold cache or first-run cost | Discard; use `warm-host` and repeat |
| Only `fresh-process` regressed | Process startup or model load | Inspect `preflight`; not a compute regression |
| `preflight` grew while `pipeline` held | Model download/session init | Separate the paths in reporting; do not average them |
| Fixture differs between runs | Different input | Re-run on the baseline fixture; discard mismatched data |
| TFM mismatch between harness and docs | `net10.0-windows10.0.19041.0` vs `net10.0` | Pin one TFM for both sides and record it |
| BDN numbers used as end-to-end evidence | BDN and `Trackdub.Benchmarks` are distinct sources of truth | Report them separately; never merge the artifacts |
| Threshold result treated as a correctness result | Conflated gates | Keep them separate; correctness runs in `pr-ready-loop.md` |
| A "small" regression dismissed | Judgment substituted for measurement | Record the number; let the human set tolerance |
| Improvement claimed with no artifact | Unverified claim | `NOT VERIFIED` until an artifact exists |

## Evidence to Collect

Per `.opencode/context/templates/evidence-report.md`:
- Baseline identity: commit sha, run id, report file path.
- Full environment capture: OS, build, TFM, GPU, driver, provider, model, manifest version and checksum, configuration, cache state, power/thermal state.
- Exact commands run, verbatim, with exit codes.
- Report paths under `$env:LOCALAPPDATA\Trackdub\benchmark-reports` and the `per-stage-matrix.csv` path.
- Repeat-by-repeat measurements, not just the aggregate.
- Per-stage delta table: stage, mode, baseline ms, candidate ms, absolute delta, percent delta, over/under tolerance.
- Attribution with its supporting measurement and its falsification test.
- Decision (accept / fix / revert) and the numbers justifying it.
- Every `NOT VERIFIED` item, listed explicitly.
- Confirmation that the correctness gate was not substituted for or by a benchmark result.

## Completion Checklist

- [ ] Baseline artifact identified and named.
- [ ] Tolerance threshold stated by the human, not assumed.
- [ ] Environment captured and confirmed to match the baseline.
- [ ] `dotnet build Trackdub.slnx -m:1` green before measuring.
- [ ] Harness command surface verified with `--help` before running.
- [ ] At least two repeat measurements taken and consistent.
- [ ] `actualProvider` and `actualModel` read from the report, not assumed.
- [ ] `preflight` and `pipeline` reported separately.
- [ ] Comparison is like-for-like on stage, provider, model, mode, and fixture.
- [ ] Attribution backed by a measurement, including the revert test where host noise is claimed.
- [ ] Per-stage isolation run via `tools/bench-per-stage.ps1` or `controlled-matrix`.
- [ ] Decision recorded: accept, fix, or revert, with numbers.
- [ ] Any fix or revert passed the Release `-warnaserror` gate via `pr-ready-loop.md`.
- [ ] BDN vs `Trackdub.Benchmarks` evidence kept separate.
- [ ] Every unverified number marked `NOT VERIFIED`.
- [ ] No estimate presented anywhere as a measurement.
