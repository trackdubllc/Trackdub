# src/Trackdub.Inference/Runtime/ExecutionProviders/README.md

## Purpose

Execution provider descriptors and capability checks for runtime planning.

## What belongs here

Windows ML / ONNX execution provider metadata used by `IRuntimePlanner` and discovery—not session construction (that lives in `Trackdub.Inference.Onnx`).

## Windows strategy (summary)

On Windows, **TensorRT RTX** uses the standalone ORT EP ABI plugin route. **Windows ML** remains the ONNX integration surface for DirectML and certified catalog EPs such as MIGraphX/OpenVINO/QNN/VitisAI. Prefer TRT RTX plugin or catalog EPs where model-compatible. **DirectML** is legacy GPU fallback via the packaged WinML route. **CPU** is the terminal fallback. See [docs/adr/ADR-0002-windows-ml-provider-strategy.md](../../../../docs/adr/ADR-0002-windows-ml-provider-strategy.md).

## What should not go here

Model task logic or `SessionOptions` / bootstrap implementation.

## Agent guidance

Keep changes scoped to this directory's purpose. If a task requires crossing boundaries, update the relevant architecture note or ADR first.
