# Four-Dimension Resource Telemetry & Benchmark Pipeline Audit

Date: 2026-10-05
Scope: Resource telemetry collection, stage validation, bounds checking, and benchmark evidence integration across Trackdub.

---

### 1. SPEC: 9.0 / 10

**Evaluation**:
1. Telemetry capture and `BenchmarkEvidenceReport` structures are established and verified.
2. Resource telemetry (CPU, working set/memory, GPU/VRAM headroom) is validated against configured bounds across pipeline stages.
3. Layering conforms to architectural boundaries: contracts in Domain/Contracts, implementation in Application/Infrastructure, wiring in Composition.
4. Test suites cover: within bounds (`Passed`), missing telemetry / unsupported platforms (`Unavailable`/`Skipped`), and threshold exceedances (`Failed`).
5. Controlled-matrix runs forward resource bounds and emit structured validation evidence.
6. Solution builds and tests pass cleanly under `TreatWarningsAsErrors=true`.

**Identified Gaps**:
1. **GPU Allocation Attribution vs Adapter Free Floor** ([IAvailableVramReader.cs](Trackdub/src/Trackdub.Contracts/Benchmarking/IAvailableVramReader.cs)): GPU telemetry measures adapter-wide available headroom (`availableVramMb` minimum floor via DXGI `QueryVideoMemoryInfo`) to detect memory pressure rather than process-isolated dedicated VRAM allocations.
   - **Resolved 2026-10-05.** Added `IProcessGpuMemoryReader` plus the `gpuBytes` evidence metric: on Windows `WindowsProcessGpuMemoryReader` sums this process's `Dedicated Usage` across its `GPU Process Memory` counter instances, giving a process-isolated attribution that `availableVramMb` deliberately does not. `--max-gpu-bytes` bounds it (the option was previously recognized but unhandled), and an unmeasurable reading with no configured budget is recorded as `Skipped` rather than downgrading a CPU-only stage. See [benchmark-evidence.md](Trackdub/docs/development/benchmark-evidence.md). **Follow-up resolved 2026-10-05.** The reading no longer only feeds telemetry: `SharedPoolOptions.UseProcessGpuMemoryReader` installs it as the shared pool's admission observation, and every accelerator admission decision floors that device's admitted usage at the process's real dedicated footprint (minus the reservations sibling devices already explain), so GPU memory the pool never reserved consumes the same per-device budget. Host-RAM buckets are unaffected, an unavailable reading leaves reservation accounting unchanged, and `TRACKDUB_SESSION_PROCESS_GPU_ADMISSION` is the explicit opt-out. See [session-pool-memory-admission.md](../reference/session-pool-memory-admission.md).
2. **Pre-flight Sanitization for Physically Impossible Bounds** ([ResourceTelemetryOptionsParser.cs](Trackdub/src/Trackdub.Benchmarks/ResourceTelemetryOptionsParser.cs)): CLI option parsing checks ranges and non-negativity, but accepts `--min-available-vram-mb` values exceeding physical adapter capacity without an upfront pre-flight warning before execution.
   - **Resolved 2026-10-05.** `ResourceBoundsPreflight` (core `src/Trackdub.Benchmarks/ResourceBoundsPreflight.cs`) compares the floor against the host's real adapter capacity — the largest adapter's dedicated plus shared video memory, read from the host's `IDeviceEnumerator` — and `ControlledDubbingBenchmarkRunner` fails the run during host setup, before any iteration is measured, with the option, the requested floor, and the capacity in the reason. An unknown capacity (no GPU adapter, an enumeration failure) skips the check instead of guessing, and vacuous maxima stay accepted because every reading satisfies them. **Follow-up 2026-10-05.** The `controlled` and `controlled-matrix` CLIs also print a host-capacity banner — detected devices with their memory, the video memory detected across GPU adapters, and the effective capacity — plus the configured floor's feasibility before the first iteration, using the same enumeration as the pre-flight so the two cannot disagree. See [benchmark-evidence.md](Trackdub/docs/development/benchmark-evidence.md).

---

### 2. DESIGN: 8.5 / 10

**Evaluation**:
- **Domain** (`src/Trackdub.Domain/Benchmarking/`): Zero external dependencies, pure immutable records (`ResourceTelemetryBounds`, `ResourceTelemetryCheck`, `ResourceTelemetryStatus`, `ResourceTelemetryValidation`, `ResourceUsageSnapshot`).
- **Contracts** (`src/Trackdub.Contracts/Benchmarking/`): Interfaces (`IResourceTelemetryValidator`, `IResourceTelemetryCollector`, `IAvailableVramReader`, `IWorkingSetSampler`) decouple callers from host diagnostics.
- **Application** (`src/Trackdub.Application/Benchmarking/`): `ResourceTelemetryValidator` encapsulates normalized CPU percentage, endpoint/sampled peak working set, and allocation delta verification.
- **Infrastructure** (`src/Trackdub.Infrastructure/`): Concrete process and OS queries isolated in `ProcessResourceTelemetryCollector`, `ProcessWorkingSetSampler`, and `WindowsAvailableVramReader`.
- **Composition** (`src/Trackdub.Composition/`): Headless composition root registers defaults via `TryAddSingleton`.

**Identified Gaps**:
1. **Direct Component Instantiation in Progress Collector** ([StageResourceTelemetryCapture.cs](Trackdub/src/Trackdub.Benchmarks/StageResourceTelemetryCapture.cs)): Progress capture instantiates `WorkingSetPeakMonitor` directly rather than through an injected monitor factory.
   - **Resolved 2026-10-06.** `WorkingSetPeakMonitorFactory` (core `src/Trackdub.Benchmarks/WorkingSetPeakMonitorFactory.cs`) is now the dedicated coordinator for peak-monitor lifetime: `StageResourceTelemetryCapture` creates its per-stage monitors through an injected `IWorkingSetPeakMonitorFactory` (defaulting to the production factory, so the public constructor is unchanged), and `ControlledDubbingBenchmarkRunner` plus `SeparationEvalRunner` create theirs through the same factory instead of `new`. Stage-boundary `Stop()` ownership is unchanged; only creation moved.
2. **Duplicated Option Extraction Branches** ([Program.cs](Trackdub/src/Trackdub.Benchmarks/Program.cs) and [ControlledStageBenchmarkMatrixOptionsParser.cs](Trackdub/src/Trackdub.Benchmarks/ControlledStageBenchmarkMatrixOptionsParser.cs)): CLI option parsing branches unpack bounds options separately in both entry points.
   - **Resolved 2026-10-06.** `ControlledBenchmarkCliBinder` (core `src/Trackdub.Benchmarks/ControlledBenchmarkCliBinder.cs`) is the single binder for the shared `controlled`/`controlled-matrix` surface: flags, value reads, `--runs`, shared string options, and resource bounds unpack in one place, with leaf validation still in `ResourceTelemetryOptionsParser`. Each command keeps only its divergent arms (`--stage` vs `--stages`, `--model` shapes, `--report-dir`). Error text and defaults are unchanged.

---

### 3. CORRECTNESS: 9.0 / 10

**Evaluation**:
- CLI command dispatch verified (`Trackdub.Benchmarks.DevHost controlled-matrix --help`).
- Full solution build passes in both Debug and Release configurations with zero warnings (`TreatWarningsAsErrors=true`).
- Test suites verified:
  - `Trackdub.Benchmarks.Tests`: 517 passed.
  - `Trackdub.Application.Tests`: 1,108 passed.
  - `Trackdub.Composition.Tests`: 275 passed.
  - `Trackdub.Domain.Tests`: 135 passed.
  - `Trackdub.Inference.Onnx.Tests`: 246 passed.
  - `Trackdub.Infrastructure.Tests`: 356 passed.
  - `Trackdub.Sdk.Tests`: 520 passed.

**Identified Gaps**:
1. **Interval Timer Precision Under CPU Load** ([WorkingSetPeakMonitor.cs](Trackdub/src/Trackdub.Benchmarks/WorkingSetPeakMonitor.cs)): The 25 ms `PeriodicTimer` cadence can experience thread-pool scheduling jitter during sustained multi-core benchmark saturation.
2. **Mock Multi-Run Timing Gap Heuristic** ([ControlledDubbingBenchmarkRunnerTests.cs](Trackdub/tests/Trackdub.Benchmarks.Tests/ControlledDubbingBenchmarkRunnerTests.cs)): The assertion `persisted - available < 500` expects tight task continuation scheduling; severe runner thread starvation can dilate async delays.

---

### 4. QUALITY: 8.5 / 10

**Evaluation**:
- Modern C# 13 / .NET 10 patterns (collection expressions, file-scoped namespaces, pattern matching).
- Zero external runtime dependencies introduced.
- Strict enforcement of `Path.Join` over banned `Path.Combine`.

**Identified Gaps**:
1. **Repetitive Exception Filters** ([WorkingSetPeakMonitor.cs](Trackdub/src/Trackdub.Benchmarks/WorkingSetPeakMonitor.cs)): Multiple catch blocks write identical diagnostics to `unavailableReason`.
2. **Legacy/Typed Telemetry Dual-Mapping** ([ControlledDubbingBenchmarkRunner.cs](Trackdub/src/Trackdub.Benchmarks/ControlledDubbingBenchmarkRunner.cs)): Dual population of legacy memory dictionary keys alongside typed structured telemetry records.
