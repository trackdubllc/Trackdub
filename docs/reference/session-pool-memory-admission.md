# ONNX session-pool memory admission

ONNX session memory admission is enabled by default. The pool starts with a 4096 MiB budget
for each accelerator device and a separate 4096 MiB host-RAM budget shared by CPU and DNNL
sessions (plus OpenVINO when CPU-proxy mode is enabled). A model-file estimate is twice the
file size plus 128 MiB, with a 64 MiB floor. Multi-graph models are admitted against the sum
of their graph estimates.

These are safety limits, not a promise that every supported model fits on every machine. For
example, a single model file around 1985 MiB already estimates above the default host budget;
larger files and multi-graph bundles need more headroom. Increase the host limit only when the
machine has enough available RAM:

```powershell
$env:TRACKDUB_SESSION_RAM_BUDGET_MB = "12288"
```

```bash
export TRACKDUB_SESSION_RAM_BUDGET_MB=12288
```

The value is in MiB and must be a positive integer. Invalid or unset values retain the 4096 MiB
default. The accelerator limit can be adjusted independently with
`TRACKDUB_SESSION_VRAM_BUDGET_MB`; it applies per device. Raising either limit permits more
resident sessions and can increase memory pressure or cause the operating system to terminate
the process if the actual workload exceeds physical memory.

## Process-isolated GPU observation

Reservations are estimates. On Windows the shared pool also accounts for this process's *actual*
dedicated GPU memory: the host registers the process-isolated reading (`gpuBytes`, summed from the
process's `GPU Process Memory` performance-counter instances) as the pool's observation source, and
each GPU admission decision uses its adapter's measured footprint when attribution is available,
or the full process total as a conservative upper bound when it is not. GPU memory the pool never reserved — driver contexts, runtime arenas,
non-pooled consumers — consumes the same per-device budget instead of being invisible to
admission.

What the observation means in practice:

- A GPU bucket admits a request only when `max(resident reservations, observed GPU usage) +
  pending reservations + request estimate` fits its budget. A mapped free adapter can admit work
  while another adapter is over budget. Each blocked bucket waits until usage drains. Each observation-blocked pass evicts at most one idle entry and re-observes,
  so evicted memory gets a chance to drain before more cache is discarded. Passes that free
  nothing count toward a fail-fast: an empty bucket throws after ~5 s of continuous stall, while
  a bucket holding live-but-unevictable entries (held leases, pins, live externals) gets ~60 s
  for turnover before the same diagnostic fires — a lease held for a whole stage still ends in
  an error, not a hang. The caller's cancellation token remains an escape hatch throughout,
  exactly as for an exhausted reservation budget.
- In-flight creates hold a pending reservation but have not allocated yet, so the process reading
  may contain partially allocated memory: pending reservations are charged on top of the observed floor, keeping
  concurrent admissions from overshooting the device budget.
- When the reader attributes usage per adapter (Windows) and the host registered its
  device-to-LUID map, each accelerator device is charged exactly its own adapter's footprint:
  usage on other adapters never blocks it and no sibling subtraction is needed. Without a
  breakdown or a mapping for the deciding GPU, the pool uses the full process total as an
  upper bound. It never subtracts estimated sibling reservations from measured bytes: an
  overestimate could otherwise hide real usage on the deciding GPU. This fallback can block
  a free adapter while another adapter holds memory; admission remains bounded by the stall
  limits above. Per-adapter attribution avoids that restriction when available.
  Both `HeadlessDubbingHost` and SDK `TrackdubBuilder.Build` register this mapping from
  `IDeviceEnumerator` during construction and release their own mapping on disposal without
  clearing a newer host's registration.
- Host-RAM buckets (CPU, DNNL, and OpenVINO CPU-proxy) and OpenVINO NPU buckets are never
  charged with dedicated GPU observations.
- An unavailable reading — no GPU, a driver that does not publish the counter set, a failing probe — leaves admission exactly as it was.

Set `TRACKDUB_SESSION_PROCESS_GPU_ADMISSION` to `0`, `false`, `off`, or `disabled` to turn the
observation off explicitly; anything else, including unset, keeps it on.

```powershell
$env:TRACKDUB_SESSION_PROCESS_GPU_ADMISSION = "0"
```

The process reading is reported as the `gpuBytes` evidence metric as well; that reporting is
independent of admission. See [benchmark-evidence.md](../development/benchmark-evidence.md).

## TTS synthesis concurrency

TTS synthesizes segments in parallel. The effective degree of parallelism is
`min(configured, VRAM-derived bound)`:

- **Configured** — `ttsMaxConcurrency` in `%LOCALAPPDATA%\Trackdub\settings.json`
  (or `TtsMaxConcurrency` in `SdkSessionOptions`). Unset or non-positive keeps the
  historical default of 4; values above 8 are clamped to 8.
- **VRAM-derived bound** — computed from the largest adapter's dedicated memory and the
  memory class of the preferred model (`NormalizedPreferredModelAlias`). One worker is
  assumed to need approximately 1 GB (small: Kokoro-82M), 4 GB (medium: CosyVoice-300M,
  Chatterbox, Qwen3-TTS-0.6B, F5) or 16 GB (large: Qwen3-TTS-1.7B) of accelerator memory,
  with each additional concurrent worker adding ~512 MB. The bound never exceeds the
  configured value and the cap never fails a run: a budget that cannot cover even one
  worker synthesizes one segment at a time.
  - When the preferred alias is not required, the planner may fall back to a model in
    another memory class. The bound does not cover that fallback model.
  - The run may be placed on a smaller adapter than the largest one. In that case the bound
    can be looser than that adapter allows.
  - A run pinned to a CPU or DNNL provider that is required is not bounded by VRAM. A
    non-required CPU pin can still fall back to an accelerator, so it stays bounded.

When adapter memory cannot be read (CPU-only machines, probe failure), the VRAM bound is
skipped and the configured value applies unchanged. The effective value is logged at the
start of every TTS stage (`TTS parallelism: N (configured …, VRAM … MB, model …)`).

```json
{ "ttsMaxConcurrency": 4 }
```

