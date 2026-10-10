# Windows ML execution providers

Tier 1: Microsoft Learn only. Inventory and restrictions checked 2026-10-10. Refresh package and driver requirements before applying them.

## Included providers

| Provider | Documented coverage |
| --- | --- |
| CPU / `CPUExecutionProvider` | Included; CPU inference needs no optional catalog EP installation |
| DirectML / `DmlExecutionProvider` | Included GPU EP, listed as legacy; DirectX 12 compatibility backstop |

CPU and DirectML need no catalog download on supported Windows versions. DirectML is in maintenance mode: Microsoft documents critical fixes/security updates rather than new operator or hardware optimization investment. [Supported EPs](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers), [sourcing comparison](https://learn.microsoft.com/windows/ai/new-windows-ml/windows-ml-eps-vs-bring-your-own), [DirectML status](https://learn.microsoft.com/windows/ai/new-windows-ml/webgpu-ep).

## Catalog providers

Catalog-acquired EPs require Windows 11 24H2/build 26100 or greater, compatible devices and supported drivers. Provider visibility is device-dependent. [Inventory](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers), [discovery](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers).

| EP | Name | Microsoft-documented requirements and limitations |
| --- | --- | --- |
| MIGraphX | `MIGraphXExecutionProvider` | AMD RDNA 3+ GPU and supported AMD driver; currently not supported for GenAI scenarios |
| NVIDIA TensorRT RTX | `NvTensorRtRtxExecutionProvider` | GeForce RTX 30-series or later; recommended NVIDIA driver/CUDA combination on the requirements page |
| Intel OpenVINO | `OpenVINOExecutionProvider` | Microsoft delegates detailed requirements/driver guidance to Intel; do not invent them under this source restriction |
| Qualcomm QNN | `QNNExecutionProvider` | Microsoft lists Snapdragon X Elite/X Plus with Hexagon NPU and a minimum driver |
| AMD VitisAI | `VitisAIExecutionProvider` | Microsoft lists a supported Adrenalin/NPU driver range, including a maximum; do not assume every newer driver works |
| WebGPU | `WebGpuExecutionProvider` | Experimental Windows ML packages, compatible DirectX 12 GPU and up-to-date drivers; absent from Windows ML 1.8.x |

Source for all rows: [Windows ML execution providers](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers). Read its live numeric requirements instead of carrying an old minimum into a recommendation.

The numbers on that page as read on 2026-10-09 are below. They are a snapshot, so re-fetch the page before quoting one.

| EP | Snapshot of documented requirement |
| --- | --- |
| MIGraphX | AMD RDNA 3 or later GPU, driver 25.10.13.09 or later |
| NvTensorRtRtx | GeForce RTX 30xx or later; recommended minimum driver 32.0.15.5585 with CUDA 12.5 |
| QNN | Snapdragon X Elite / X Plus Hexagon NPU, driver 30.0.140.0 or later |
| VitisAI | Minimum Adrenalin 25.6.3 with NPU driver 32.00.0203.280; maximum Adrenalin 25.9.1 with NPU driver 32.00.0203.297 |
| WebGPU | Microsoft.Windows.AI.MachineLearning 2.4.66-preview or later; package family `Microsoft.WinML.ONNX.WebGPU.EP.2` |

Per-EP release history is on the Windows ML Execution Provider Releases wiki that the Learn page links to.

## Hardware classes

Microsoft maps OpenVINO to Intel CPU/GPU/NPU, QNN to Qualcomm GPU/NPU, VitisAI to AMD NPU, MIGraphX to AMD GPU, and NvTensorRtRtx to NVIDIA GPU. An available provider does not establish that every hardware class or model works on a particular machine. [Silicon-to-EP mapping](https://learn.microsoft.com/windows/ai/new-windows-ml/accelerate-ai-models).

## WebGPU-specific limitations

- Requires experimental packages; a stable package with a higher version number does not establish experimental functionality.
- `EnsureAndRegisterCertifiedAsync()` does not install WebGPU. Its documented lifecycle explicitly enumerates, prepares/installs, registers, binds and unregisters it after sessions are disposed.
- Documented precision support is FP32/FP16 and INT4/INT8 for LLMs. Non-LLM INT8 can run at higher precision or fall back to CPU.
- Unsupported operators can partition execution across GPU and CPU, affecting performance.
- Not currently recommended for performance-critical or highly optimized production workloads.

Source: [WebGPU EP](https://learn.microsoft.com/windows/ai/new-windows-ml/webgpu-ep).

## Provider options and model limits

For setup, use [model configuration](model-setup-and-configuration.md). Microsoft Learn's inventory does not enumerate every provider's operator, backend-option, shape or quantization restrictions. If a requested detail is only in a linked external vendor/ORT page, mark it not established under the Microsoft-Learn-only rule. Do not transfer one provider's options to another.

## Licensing and scope

Each optional vendor EP has separate terms from Windows ML. Read [third-party notices](https://learn.microsoft.com/windows/ai/new-windows-ml/third-party-notices); that listing does not determine a model's commercial-use or redistribution rights.

CUDA, classic TensorRT, DNNL and CoreML are not entries in this Windows ML catalog inventory. Do not construct Trackdub routing or compatibility claims about them from this skill. [Inventory](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers).
