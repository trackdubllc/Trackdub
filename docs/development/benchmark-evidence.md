# Local benchmark evidence

The benchmark backend stores versioned, path-free JSON reports under the local Trackdub user data directory and indexes them in the user benchmark database. Ordinary project stage runs create **observations**. Explicit controlled fixture runs create **benchmarks**. Only compatible completed benchmarks belong in performance comparisons. A skip, failed stage, partial pipeline, unavailable measurement, or unapproved provider fallback remains visible as raw evidence and is excluded from aggregates.

Run the developer host from a new process for each fresh-process sample:

```powershell
dotnet run --project src/Trackdub.Benchmarks.DevHost -c Release -f net10.0-windows10.0.19041.0 -- controlled <fixture> --output <local-run-directory> --sha256 <fixture-sha256> --stage asr --provider Cpu --mode fresh-process
```

`--reuse-engine-cache` distinguishes a new process with a compatible existing engine cache from the default new isolated engine cache. `--mode warm-host` times a rerun after warmup in one host. `--mode artifact-resume` primes artifacts, then times a resume attempt. A resume can legitimately skip with `EXISTING_ARTIFACTS_VALID`; that is not a speed sample. `--stage` may be omitted to attempt the full dubbing pipeline. Stage values come from `DubbingPipelineStages.ExtendedStageOrder`, such as `asr` and `audio-preparation`.

Reports record UTC endpoints and monotonic durations, stage status and reason, persisted actual model/provider, fixture hash, runtime versions, process memory, and available phase spans. Null means unavailable. First transcript and audio timings require a usable transcript or playable take, respectively. GPU memory remains null without a reliable probe. Reports contain no transcript, media, or absolute source path. Project histories remain intact; only automatic observation history is bounded to 90 days or 5,000 records.

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

## Baseline fixture set, 2026-09-23

The fixtures are machine-local and are not checked into the repository. Their local manifest is `%LOCALAPPDATA%\Trackdub\benchmark-fixtures\baseline-v1\manifest.json`. This table identifies contents without publishing media paths.

| Fixture | Duration | SHA-256 |
|---|---:|---|
| Short | 8 s | `c4640c3f8062b4d928eeef25c52f845f4867c10f26aeb4f5d5ce6be1c295bd85` |
| Multi-speaker | 24.5 s | `35f66fc263e5d907f981f6062d7cfe4bfa23faf468a66062a0a07bcc4fa68513` |
| Long-form | 933.8 s | `eef434a89dfdd017e465a9af35289db22a11873124956ce14300ef98bd1fe898` |
| Silence | 12 s | `e7df589267ecde30673ffcdf9da443f56ea1e698b2d6c71ac637d31d501ec5eb` |

Each run's full JSON is retained in `%LOCALAPPDATA%\Trackdub\benchmark-reports\<run-id>.json`. These are initial raw samples, not comparison medians:

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
