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
every accelerator admission decision then floors that device's admitted usage at the process's real
dedicated footprint. GPU memory the pool never reserved — driver contexts, runtime arenas,
non-pooled consumers — consumes the same per-device budget instead of being invisible to
admission.

What the observation means in practice:

- A process already at or above the accelerator budget admits no new accelerator session until its
  real usage drains. Idle pooled sessions are evicted first, and the wait is re-evaluated every
  50 ms, so a genuine release unblocks the caller; the caller's cancellation token remains the
  escape hatch, exactly as for an exhausted reservation budget.
- The reading is process-wide and cannot be attributed to an adapter, so it is conservative on a
  multi-GPU host: only the reservations the pool's *other* devices already explain are subtracted.
- Host-RAM buckets (CPU, DNNL, and OpenVINO CPU-proxy) are never charged with it.
- An unavailable reading — no GPU, a driver that does not publish the counter set, a GPU-idle
  process, or a failing probe — leaves admission exactly as it was.

Set `TRACKDUB_SESSION_PROCESS_GPU_ADMISSION` to `0`, `false`, `off`, or `disabled` to turn the
observation off explicitly; anything else, including unset, keeps it on.

```powershell
$env:TRACKDUB_SESSION_PROCESS_GPU_ADMISSION = "0"
```

The process reading is reported as the `gpuBytes` evidence metric as well; that reporting is
independent of admission. See [benchmark-evidence.md](../development/benchmark-evidence.md).
