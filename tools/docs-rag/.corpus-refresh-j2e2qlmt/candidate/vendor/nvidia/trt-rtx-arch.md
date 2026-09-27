Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/architecture-overview.html

Architecture Overview
This section provides an overview of TensorRT-RTX’s architecture, design principles, and ecosystem. It introduces key concepts and complementary tools for deploying optimized inference on NVIDIA RTX GPUs across desktops, laptops, and workstations.
TensorRT for RTX (TensorRT-RTX) is a specialization of [NVIDIA TensorRT](<https://developer.nvidia.com/tensorrt>) for the RTX product line. Like TensorRT, it provides a deep learning inference optimizer and runtime. Unlike TensorRT, TensorRT-RTX performs just-in-time (JIT) compilation on the end-user device, which simplifies deployment across a diverse set of RTX GPUs without per-device ahead-of-time builds in your release pipeline.
For runtime object lifetimes, threading, memory contracts, and engine compatibility, refer to [How TensorRT-RTX Works](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#works>) .
Two-Phase Compilation
After you train a model in a framework of your choice, TensorRT-RTX compiles it for high-throughput, low-latency inference on the end-user GPU. Compilation proceeds in two phases:
Phase
When
Duration
Output
Portability
Phase 1: Ahead-of-Time (AOT) optimization
During install, first launch, or an offline build step (before inference)
Typically 20–30 seconds for most models; up to ~60 seconds for complex models
A portable TensorRT-RTX engine file (JIT-able engine)
Portable across supported RTX GPU models; AOT build can run on CPU
Phase 2: Just-in-Time (JIT) compilation
First inference invocation on the end-user GPU
Typically under 5 seconds for most models on first run
An executable inference plan with GPU-specific kernel choices
Optimized for the user’s specific GPU; [runtime caching](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) can persist compiled kernels across runs
This two-phase approach trades a small amount of first-run startup time for portability: you ship a single engine from the AOT phase, and the JIT phase specializes it for the GPU it actually runs on, so your release pipeline does not have to pre-compile one engine per target device. Benchmark your own model to compare against a device-specific build. Refer to [Optimizing Performance](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/optimization.html#optimize-performance>) .
Complementary Software
Tool
Description
[Windows ML](<https://blogs.windows.com/windowsdeveloper/2025/09/23/windows-ml-is-generally-available-empowering-developers-to-scale-local-ai-across-windows-devices/>)
Default execution provider on RTX GPUs for production Windows apps. TensorRT-RTX is the default EP when Windows ML runs on supported RTX hardware.
[ONNX Runtime TensorRT-RTX Execution Provider](<https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html>)
Run ONNX models through ONNX Runtime with TensorRT-RTX acceleration. Use this path when your application already integrates ONNX Runtime.
[ONNX Runtime GenAI](<https://github.com/microsoft/onnxruntime-genai/blob/main/src/python/py/models/README.md>)
LLM workflows on RTX with TensorRT-RTX as an execution provider. Olive recipes such as the [Qwen2.5 example](<https://github.com/microsoft/olive-recipes/tree/main/Qwen-Qwen2.5-7B-Instruct/NvTensorRtRtx>) show end-to-end optimization paths.
[Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>)
Accelerate torch.compile() workloads with the tensorrt backend through the torch-tensorrt-rtx package.
[Model Optimizer](<https://github.com/NVIDIA/Model-Optimizer>)
Quantization and compression for models exported to ONNX and deployed with TensorRT-RTX. Datatype support is listed in the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) .
[NVIDIA Nsight Systems](<https://developer.nvidia.com/nsight-systems>)
System-wide profiling integrated with TensorRT-RTX NVTX ranges. Refer to [Performance Benchmarking](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/benchmarking.html#performance-benchmarking>) .
[ONNX GraphSurgeon](<https://github.com/NVIDIA/TensorRT/tree/main/tools/onnx-graphsurgeon>) / [Polygraphy](<https://github.com/NVIDIA/TensorRT/tree/main/tools/Polygraphy>)
Edit and simplify ONNX graphs before parsing. Refer to the [ONNX GraphSurgeon API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/onnx-graphsurgeon-api.html#onnx-graphsurgeon-api>) and [Polygraphy API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/polygraphy-api.html#polygraphy-api>) pages.
ONNX
TensorRT-RTX’s primary means of importing a trained model is the [ONNX](<https://onnx.ai/>) interchange format. TensorRT-RTX ships with an ONNX parser library and the tensorrt_rtx command-line tool for building engines from .onnx files. You can also construct networks through the native C++ or Python API, or run models through ONNX Runtime and Windows ML execution providers.
Entry path
When to use
Next step
ONNX export + ONNX parser
Most training frameworks (PyTorch, TensorFlow, JAX, and others)
[ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) , then [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) (CLI verify loop), [Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) (deployment), or the [C++](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/c-api-docs.html#c-api-docs>) / [Python](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/python-api-docs.html#python-api-docs>) API walkthroughs
ONNX Runtime execution provider
Applications already running models through ONNX Runtime or Windows ML
[TensorRT-RTX Execution Provider](<https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html>) documentation
Native network definition API
Maximum control over graph construction and weights
[Creating A Network Definition Using The C++ API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/c-api-docs.html#create-network-c>) , [Creating A Network Definition Using The Python API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/python-api-docs.html#create-network-python>) , then [Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>)
PyTorch ``torch.compile`` backend
PyTorch-centric workflows without manual ONNX export
[Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>)
ONNX conversion is all-or-nothing: every operation in the model must be supported by TensorRT-RTX. Refer to [Operators](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/operators.html#operators>) for the ONNX operator catalog.
Relation to NVIDIA TensorRT
TensorRT-RTX optimizes CNN, diffusion, and speech models expressed in ONNX or native C++ APIs on NVIDIA RTX GPUs. Unlike the [NVIDIA TensorRT Inference library](<https://developer.nvidia.com/tensorrt>) , TensorRT-RTX does not target datacenter, edge, or embedded GPU platforms.
TensorRT-RTX exposes a subset of APIs derived from TensorRT and shares the same namespace, so existing TensorRT applications for RTX devices can port by linking to the new library. For when to choose TensorRT versus TensorRT-RTX, the migration checklist, and API differences, refer to the [Porting Guide for TensorRT Applications](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>) . Start with [Choosing an Inference Solution](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting-choosing-inference>) and [Migration Checklist](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting-migration-checklist>) .
TensorRT-RTX does not support native LLM deployment out of the box. When used as an execution provider with Windows ML or ONNX Runtime GenAI, TensorRT-RTX optimizes LLM inference on RTX GPUs with compute capability 8.6 or later (Ampere and later). LLMs from Windows [AI Foundry Local](<https://learn.microsoft.com/en-us/azure/ai-foundry/foundry-local/get-started>) can use the TensorRT-RTX execution provider. On RTX Turing GPUs (20-series), LLM support through TensorRT-RTX is planned for a future release; use the CUDA Execution Provider as a fallback path on Turing today.
Note
TensorRT-RTX does not yet support framework integrations with [NVIDIA TensorRT-LLM](<https://github.com/NVIDIA/TensorRT-LLM>) , [Torch-TensorRT](<https://github.com/pytorch/TensorRT>) , [TensorFlow-TensorRT](<https://github.com/tensorflow/tensorrt>) , and [NVIDIA Triton Inference Server](<https://docs.nvidia.com/deeplearning/triton-inference-server/user-guide/docs/index.html>) .
Code Analysis Tools
On Linux, use [API Capture and Replay](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/capture-replay.html#capture-replay>) to record and replay TensorRT-RTX API sequences during engine building without the original application or model source. For broader diagnostic workflows, refer to the [Troubleshooting](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/troubleshooting.html#troubleshooting>) section.
API Versioning
TensorRT-RTX version numbers (MAJOR.MINOR.PATCH) follow [Semantic Versioning 2.0.0](<https://semver.org/#semantic-versioning-200>) for public APIs and library ABIs. Serialized engines are tied to the TensorRT-RTX version that built them. Refer to [Engine Compatibility](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#version-compat>) and [Compatibility Checks](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#compatibility-checks>) before deserializing engines across releases or GPUs.
Deprecation Policy
Deprecation informs developers that NVIDIA no longer recommends some APIs and tools and plans to remove them. TensorRT-RTX has the following deprecation policy (similar to the [TensorRT deprecation policy](<https://docs.nvidia.com/deeplearning/tensorrt/latest/architecture/architecture-overview.html#deprecation>) ):
Deprecation notices are communicated in the [Release Notes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/release-notes.html#release-notes>) .
When using C++ API:
API functions are marked with the TRT_DEPRECATED_API macro.
Enums are marked with the TRT_DEPRECATED_ENUM macro.
All other locations are marked with the TRT_DEPRECATED macro.
Classes, functions, and objects will have a statement documenting when they were deprecated.
When using the Python API, deprecated methods and classes will issue deprecation warnings at runtime if they are used.
TensorRT-RTX provides a 12-month migration period after the deprecation.
APIs and tools continue to work during the migration period.
After the migration period ends, NVIDIA removes APIs and tools in a manner consistent with [semantic versioning](<https://semver.org/>) .
Hardware Support Lifetime
TensorRT-RTX targets NVIDIA RTX GPUs from Turing (compute capability 7.5) through Blackwell (compute capability 12.0 / 12.1). Portable AOT engines target Ampere and later by default; Turing requires an explicit compute-capability setting at build time. Refer to the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) for supported hardware, operating systems, and dependency versions.
Support
Support, resources, and information about TensorRT-RTX can be found on the [TensorRT-RTX GitHub repository](<https://github.com/NVIDIA/TensorRT-RTX>) and the [NVIDIA Developer TensorRT forum](<https://forums.developer.nvidia.com/c/ai-data-science/deep-learning/tensorrt/>) .
Reporting Bugs
If you encounter problems, gather TensorRT-RTX version, GPU model, driver and CUDA versions, and relevant log output, then open an issue on the [TensorRT-RTX GitHub repository](<https://github.com/NVIDIA/TensorRT-RTX/issues>) or post on the [developer forum](<https://forums.developer.nvidia.com/c/ai-data-science/tensorrt-for-rtx/738>) . Refer to [Troubleshooting](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/troubleshooting.html#troubleshooting>) for diagnostic workflows.
