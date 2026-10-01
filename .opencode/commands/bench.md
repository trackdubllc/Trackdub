---
description: "Run DubBench/Benchmarks, diff against a baseline, and attribute any regression"
agent: subagents/benchmark-perf
---

Benchmark run + baseline diff + regression attribution.

**Baseline identifier:** `$1` — **REQUIRED**. A tag, SHA, saved-commit baseline id, or a path to a stored report set. If `$1` is empty: **stop.** List the candidate baselines you can actually see (tags, recent SHAs, existing report artifacts) and ask which one to use. **Do not invent, guess, or auto-select a baseline.**

**Scope (optional):** `$2` — workload or stage to measure, e.g. `translate`, `tts`, `vad`, `asr`, `separation`, `alignment`, a fixture path, a `--mode`, or `all`.

## Record what is being measured

```
$ git log --oneline -5
$ git status --short
```

Capture HEAD SHA, working-tree state, and whether the tree is dirty. A dirty tree makes the result non-reproducible — say so in the report.

## Step 1 — identify the harness

Choose the right harness for the question. Do not mix them.

| Harness | Use for |
|---|---|
| `src/Trackdub.Benchmarks` | **source of truth** for controlled end-to-end pipeline evidence and `BenchmarkEvidenceReport`. Do not replace or merge with BDN artifacts. |
| `src/Trackdub.Benchmarks.DevHost` | the controlled runner: `controlled`, `controlled-matrix <fixture> --output <dir>` |
| `src/Trackdub.Benchmarks.Micro` | BenchmarkDotNet. Pure CPU, tensor, tokenizer, and opt-in real-model ONNX measurements only. |
| `src/DubBench`, `src/DubBench.DevHost` | the DubBench path (`DubBench → Benchmarks, Domain, Inference, Inference.Onnx`) |
| `tools/bench-per-stage.ps1` | Windows per-stage sweep over the DevHost `controlled` path |
| `tools/bench-smoke-verdict-ab.ps1` | Windows A/B smoke verdict over the controlled path |
| `tools/trackdub-optimize.ps1` / `.sh` | optimization driver, cross-platform |
| `scripts/ci/run_benchmarkdotnet_baseline.py` | saved-commit BDN comparisons |

```bash
# Controlled per-stage matrix (comparable per-stage runs; each stage's evidence preserved separately)
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- controlled-matrix <fixture> --output <dir>

# BDN — Release, deterministic inputs, GlobalSetup; model/tokenizer/file/session init OUTSIDE measured methods
dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- --list flat

# Filter builds
dotnet build Trackdub.Inference.slnx -m:1
dotnet build Trackdub.Sdk.slnx -m:1
```

## Step 2 — run current

Release configuration. Same machine, same fixture, same runtime flavor. Record the exact reproduce command verbatim in the report — it must be copy-pasteable as-is.

## Step 3 — diff vs baseline

Compare like-for-like benchmark names only. Keep threshold results separate from correctness tests; a benchmark pass is not a test pass.

Report, per metric: baseline value, current value, absolute delta, percentage delta, and whether the delta exceeds the threshold.

## Step 4 — attribute

For every delta beyond noise, attribute it to exactly one primary cause and state the evidence:

- **GPU** — EP/device selection, VRAM pressure, thermal throttling, driver/CUDA/TRT version, GPU contention from another process.
- **CPU** — core count, thread-pool saturation, GC pauses, allocator behavior, code path/allocation change.
- **IO** — model/cache load time, fixture read, disk contention, first-run vs warm cache (`--reuse-engine-cache` changes this).
- **Model-or-config change** — different model id, quantization, `TrackdubOrtRuntimeFlavor` (Ort / Dnnl / WinML), provider, thread settings, or a config default that differs between baseline and current.

If the attribution cannot be evidenced, write `ATTRIBUTION NOT VERIFIED` and name what would settle it. Do not guess.

## Step 5 — report

```markdown
# Benchmark: <workload> vs <baseline-id>

Commit: <sha> (<dirty|clean>)
Harness: <harness + exact command>
Machine: <cpu/gpu/os>

| Metric | Baseline | Current | Delta | % | Threshold | Verdict |
|---|---|---|---|---|---|---|

## Attribution
- <metric>: <GPU|CPU|IO|model-or-config> — <evidence>
- <metric>: ATTRIBUTION NOT VERIFIED — prove with: <command>

## Reproduce
<exact command, verbatim>
```

## Hard rules

- **Report measured numbers only.** If the run failed, the artifact is missing, or the baseline is absent, report `NOT VERIFIED`. Never estimate, interpolate, extrapolate, or recall a number from a previous session. A number without a source artifact does not go in the table.
- Never invent a comparison. No baseline means no delta, and you say so rather than implying "no regression".
- Never claim a regression was fixed without a measured post-fix run at the same settings.
- **Benchmarks are NOT part of the Release `-warnaserror` CI gate.** They are a separate, explicitly invoked check. BDN is not run on pull-request CI: CPU runs are manually/nightly triggered, and real-model ONNX plus saved-commit comparisons are opt-in via `.github/workflows/benchmark-dotnet.yml`. A green `/validate` says nothing about performance, and a green benchmark says nothing about correctness.
- Do not tune, optimize, or edit code as part of this command. Measure and report; the user decides what to change.