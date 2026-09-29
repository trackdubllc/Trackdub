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
