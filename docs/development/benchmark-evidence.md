# Local benchmark evidence

The benchmark backend stores versioned, path-free JSON reports under the local Trackdub user data directory and indexes them in the user benchmark database. Ordinary project stage runs create **observations**. Explicit controlled fixture runs create **benchmarks**. Only compatible completed benchmarks belong in performance comparisons. A skip, failed stage, partial pipeline, unavailable measurement, or unapproved provider fallback remains visible as raw evidence and is excluded from aggregates.

Run the developer host from a new process for each fresh-process sample:

```powershell
dotnet run --project src/Trackdub.Benchmarks.DevHost -c Release -f net10.0-windows10.0.19041.0 -- controlled <fixture> --output <local-run-directory> --sha256 <fixture-sha256> --stage asr --provider Cpu --mode fresh-process
```

`--reuse-engine-cache` distinguishes a new process with a compatible existing engine cache from the default new isolated engine cache. `--mode warm-host` times a rerun after warmup in one host. `--mode artifact-resume` primes artifacts, then times a resume attempt. A resume can legitimately skip with `EXISTING_ARTIFACTS_VALID`; that is not a speed sample. `--stage` may be omitted to attempt the full dubbing pipeline. Stage values come from `DubbingPipelineStages.ExtendedStageOrder`, such as `asr` and `audio-preparation`.

Reports record UTC endpoints and monotonic durations, stage status and reason, persisted actual model/provider, fixture hash, runtime versions, process memory, and available phase spans. Null means unavailable. First transcript and audio timings require a usable transcript or playable take, respectively. Reports contain no transcript, media, or absolute source path. Project histories remain intact; only automatic observation history is bounded to 90 days or 5,000 records.

Resource telemetry distinguishes two GPU readings that are not interchangeable. `availableVramMb` is adapter-wide free headroom from DXGI `QueryVideoMemoryInfo`, so it moves with every other process on the same GPU and detects memory pressure rather than attributing bytes. `gpuBytes` is this process's own dedicated GPU memory, read on Windows from the per-process `GPU Process Memory` performance counter set; it is process-isolated, so a stage's GPU cost can be attributed to that stage. Either reading is null when the platform or driver cannot report it, and `--max-gpu-bytes` bounds `gpuBytes` as an inclusive maximum. A `--min-available-vram-mb` floor above the host's total video memory (dedicated plus shared, across its adapters) can never be met by any run, so it is rejected during host setup — before any iteration is measured — instead of being accepted and reported afterwards; a host whose capacity cannot be enumerated skips that check rather than guessing. The process reading is also wired into the shared ONNX session pool: on Windows the host registers it with the pool, which floors each accelerator device's admitted usage at the process's real dedicated footprint, so GPU memory the pool never reserved still consumes the same per-device budget. Set `TRACKDUB_SESSION_PROCESS_GPU_ADMISSION=0` to opt out; see [session-pool memory admission](../reference/session-pool-memory-admission.md).

`controlled` and `controlled-matrix` print a host-capacity banner before the first iteration: the detected devices with the memory each reports, the video memory detected across the GPU adapters, and the effective capacity the pre-flight compares a `--min-available-vram-mb` floor against, followed by that floor's feasibility. The banner uses the same device enumeration as the pre-flight, so it cannot disagree with it, and it is diagnostic only — a host it cannot enumerate leaves the capacity unknown and never changes the run's outcome. Help output is unchanged.

## Stage-focused matrix

Use the matrix command when the goal is a comparable timing row for each
pipeline stage rather than a single full-pipeline result:

```powershell
dotnet run --project src/Trackdub.Benchmarks.DevHost -c Release -f net10.0-windows10.0.19041.0 -- controlled-matrix <fixture> --output <matrix-directory> --stages vad,diarization,asr,translation,tts,export
```

With no `--stages`, `controlled-matrix` runs the canonical extended stage
catalog. Use `--model stage=alias` for stage-specific model pins, for example
`--model asr=whisper-small,tts=kokoro`. The matrix runs each stage through the
existing controlled benchmark path, including prerequisites, and writes one
matrix report containing the individual `BenchmarkEvidenceReport` objects.
It is intentionally separate from BenchmarkDotNet microbenchmarks; the matrix
measures real stage execution and evidence semantics, while BDN measures
small operations and allocations.

Working-set limits use an interval sampler at a 25 ms cadence, starting at the
stage boundary and stopping at its terminal event. The reported peak is the
maximum observed sample plus the two endpoint samples; short spikes between
polls may be missed. A sampler failure is explicit `Unavailable` evidence
unless a known endpoint already breaches the configured limit. The benchmark
report also records a run-level sampled peak across setup, pipeline, and
teardown. This samples the benchmark process only, including concurrent work
and excluding child processes.

CI runs a deterministic, three-iteration mock controlled-matrix CPU budget
gate via `scripts/ci/check_controlled_matrix_cpu_budget.py`. It enforces the
normalized CPU limit of 95% per measured stage sample and verifies the typed
report contains all expected iterations; it does not require models, GPUs, or
machine-local fixtures.

A mock provider matrix (`matrix <fixture> --output <dir> --mock --providers cpu,directml,tensorrt`)
simulates a fixed per-provider latency rather than measuring one: each canonical
stage waits a base delay scaled by the provider's multiplier — 1.0 for CPU, 0.6 for
OpenVINO, 0.5 for DirectML, 0.25 for TensorRT — so the comparison a mock run
demonstrates is a 2x DirectML and 4x TensorRT speedup over the CPU baseline. The
measured ratios the report computes are not that contract: a mock run's own fixed
cost (host setup, telemetry sampling, the simulated allocations) is of the same
order as the simulated gap, so a provider's measured percentile can even invert the
order the simulation intended. A non-dry mock run is therefore the deterministic
mode: each comparison row in `execution-provider-matrix.json` also carries
`SimulatedLatencyBudgetMilliseconds`, the total simulated stage latency that
provider was configured to wait (1000 ms for CPU, 500 for DirectML, 250 for
TensorRT), and the markdown export lists the same budgets beneath the table.
Dividing the baseline row's budget by another row's gives the speedup the
simulation demonstrates, and a measured percentile cannot fall below its own
budget, so the contract is assertable from the report itself. A dry run waits no
simulated delay and a real run simulates nothing, so neither reports a budget, and
the field is omitted from JSON when absent so real reports keep their shape.
Latency-to-ratio arithmetic remains covered by the pure comparison tests; real
runs measure real execution, and their ratios are exactly what the report states.

## Baseline fixture set, 2026-09-23

The fixtures are machine-local and are not checked into the repository. Their local manifest is `%LOCALAPPDATA%\Trackdub\benchmark-fixtures\baseline-v1\manifest.json`. This table identifies contents without publishing media paths.

| Fixture | Duration | SHA-256 |
|---|---:|---|
| Short | 8 s | `c4640c3f8062b4d928eeef25c52f845f4867c10f26aeb4f5d5ce6be1c295bd85` |
| Multi-speaker | 24.5 s | `35f66fc263e5d907f981f6062d7cfe4bfa23faf468a66062a0a07bcc4fa68513` |
| Long-form | 933.8 s | `eef434a89dfdd017e465a9af35289db22a11873124956ce14300ef98bd1fe898` |
| Silence | 12 s | `e7df589267ecde30673ffcdf9da443f56ea1e698b2d6c71ac637d31d501ec5eb` |

Each run's full JSON is retained in `%LOCALAPPDATA%\Trackdub\benchmark-reports\<run-id>.json`. These initial raw samples did not capture the producing Git revision or build identity. Keep them as historical observations; do not use them as a versioned baseline or in like-for-like comparisons, comparison medians, or release budgets:

| Fixture / run | Mode / stage | Outcome | Timed pipeline |
|---|---|---|---:|
| Short `40aaa1e374c2465f8bcde775ba08e323` | Fresh process, compatible cache / ASR | Completed, requested and actual CPU, `qwen3-asr-0.6b` | 15,510 ms |
| Silence `93114ca2f2ab4ec8866f45e1f4ab96f8` | Fresh process, isolated engine cache / audio preparation | Completed | 18,088 ms |
| Multi-speaker `e14ecc07377e43a7a7ce5fc4c0f29dec` | Fresh process, isolated engine cache / audio preparation | Completed | 15,564 ms |
| Long-form `06600a21eac14be787fb459a9ecc4e64` | Fresh process, isolated engine cache / audio preparation | Completed | 164,224 ms |
| Short `033e3082f4544fc7911feac78de9851c` | Warm host / audio preparation | Completed | 3,165 ms |
| Short `36b343baec5d4f86a1bce26c7c354520` | Artifact resume / audio preparation | Skipped: `EXISTING_ARTIFACTS_VALID` | 482 ms, excluded |

The focused ASR sample spent 329,650 ms preparing prerequisite stages; that time is separate from its 15,510 ms ASR pipeline span. A prior full-pipeline attempt (`91b3193af93a49c9ae0310ebc700fa66`) reached translation failure, then skipped TTS and export. Its provider labels came from a UI display projection and were invalidated; it is excluded from comparisons. A corrected full-pipeline attempt is being recorded separately.

These runs used .NET 10.0.12, Windows 10.0.26200 x64, ONNX Runtime 1.30.0.0, and a host reporting 63 GB available memory. A separate local CIM query identified an AMD Ryzen 7 5700X3D CPU, NVIDIA GeForce RTX 5070 GPU, and 68,613,902,336 bytes physical RAM. Automatic CPU/GPU model-name discovery in the reports returned `Unknown` on this Windows installation. The reports do not claim cleared OS/driver caches or measured GPU memory. More repetitions on each compatible fixture, model, provider, and cache mode are needed before prioritizing findings in `Fusion_Performance_Audit.md`.

## Revision-pinned replacement samples, 2026-09-28

The producing checkout was clean at `4eea0cc954f745ada80cac2be9e8992f93851883`. The `Trackdub.Benchmarks.DevHost` Release build for `net10.0-windows10.0.19041.0` passed with zero warnings and errors. Every command below used `dotnet run --no-build` from that build; the host DLL SHA-256 was `F054C1A51B37C9F5C7EA2218CC4D66EE037708DD6404C02C2571B626F1113902`. All four input hashes matched the fixture manifest above before the runs. The raw reports are retained under `%LOCALAPPDATA%\Trackdub\benchmark-reports\<run-id>.json`.

The command form was `dotnet run --project src/Trackdub.Benchmarks.DevHost -c Release -f net10.0-windows10.0.19041.0 --no-build -- controlled <fixture> --output <local-output-directory> --stage <stage> --mode <mode> --sha256 <fixture-hash>`. The ASR invocation also supplied `--provider Cpu --reuse-engine-cache --source-language en`; no `--reuse-engine-cache` flag was supplied for the isolated-cache runs. Each row was launched as a separate process.

| Fixture / run id | Mode / stage | Outcome | Timed pipeline |
|---|---|---|---:|
| Short `872e838f131844d1a8c612a7c761a31c` | Fresh process, compatible cache / ASR | Completed; actual `qwen3-asr-0.6b` on CPU | 60,417 ms |
| Silence `4b65ad4c1658474c90c6128907f7355a` | Fresh process, isolated engine cache / audio preparation | Completed | 7,947 ms |
| Multi-speaker `d250be27e8e1429ca77199048ace0866` | Fresh process, isolated engine cache / audio preparation | Completed | 6,974 ms |
| Long-form `fd9b830caf3546aab9496c4a518a0f54` | Fresh process, isolated engine cache / audio preparation | Completed | 100,791 ms |
| Short `38bdf0786ae94d53aedfc2d0996e5bd1` | Warm host / audio preparation | Completed | 2,459 ms |
| Short `9c32c7f661a04b689d2683dac3b31cfe` | Artifact resume / audio preparation | Skipped: `EXISTING_ARTIFACTS_VALID`; excluded from throughput comparisons | 389 ms |

The ASR sample spent 30,251 ms preparing prerequisites before its timed pipeline. The reports recorded .NET 10.0.12, Windows 10.0.26200 x64, and ONNX Runtime 1.30.0.0. These are source-revision-pinned initial observations, not comparison medians or evidence of improvement over the unversioned September 23 samples. The retained reports do not record per-run model-manifest IDs or `WindowsMlExecutionDevicePolicy`, so this set does not satisfy the full per-run provenance requirement in the profiling report. OS/driver caches were not cleared, and single runs do not establish a stable baseline. Capture the source revision, clean/dirty checkout state, build artifact hash, fixture hashes, model-manifest IDs, EP policy, command options, and run IDs with future measurements.
