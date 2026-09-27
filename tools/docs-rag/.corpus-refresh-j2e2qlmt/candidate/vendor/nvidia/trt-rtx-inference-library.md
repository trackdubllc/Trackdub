Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/index.html

Inference Library Overview
The Inference Library section documents how to build TensorRT-RTX engines, run inference with the C++ and Python APIs, and apply advanced runtime features for deployment on RTX GPUs. Use this overview to choose the right guide for your integration stage.
About the Inference Library
TensorRT-RTX exposes the same core builder and runtime concepts as TensorRT: network definition, serialized engines, execution contexts: optimized for portable deployment with ahead-of-time (AOT) and just-in-time (JIT) compilation on end-user RTX hardware.
For installation and first success, start with [Installation Guide Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installation-overview.html#installation-overview>) , [Installing TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html#installing>) , and the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) . After Quick Start succeeds, use [Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) for deployment planning. For runtime object lifetimes and threading, refer to [How TensorRT-RTX Works](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#works>) . When migrating from TensorRT, begin with the [Porting Guide for TensorRT Applications](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>) .
Integration Path Overview
Install and verify : Prerequisites → install → [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) → [Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) for bundling and portability
Learn the runtime workflow : Build, deserialize, and run a simple engine with the native API → [Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>)
Integrate from PyTorch : Accelerate torch.compile() workloads with the TensorRT-RTX backend → [Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>)
Follow language-specific walkthroughs : Step-by-step C++ or Python build and inference flows → [C++ API Documentation](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/c-api-docs.html#c-api-docs>) or [Python API Documentation](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/python-api-docs.html#python-api-docs>)
Apply advanced features : Quantization, dynamic shapes, CUDA graphs, runtime cache, and more → topics in What’s in This Section below
Optimize and debug : Performance tuning, SCG, CPU-only engines, porting, and capture/replay as needed
For ONNX export before any API integration, refer to [ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) .
What’s in This Section
This inference library is organized into the following guides:
Using the Native Runtime API
End-to-end tutorial for building an engine and running inference with the C++ or Python API.
→ [Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>)
C++ API Documentation
Detailed C++ workflow: network creation, ONNX import, engine build, deserialization, and inference.
→ [C++ API Documentation](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/c-api-docs.html#c-api-docs>)
Python API Documentation
Python equivalents for parsing ONNX, building engines, and executing inference.
→ [Python API Documentation](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/python-api-docs.html#python-api-docs>)
Using TensorRT-RTX via PyTorch
Compile PyTorch models with torch.compile() and the TensorRT-RTX backend through Torch-TensorRT-RTX.
→ [Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>)
Advanced Topics
Weight streaming, engine compatibility, refitting, and deprecated timing-cache migration.
→ [Advanced Topics](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/advanced.html#advanced>)
Work With Quantized Types
Guidance for explicit quantization, strongly typed networks, and quantized tensor types in TensorRT-RTX.
→ [Work With Quantized Types](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-quantized-types.html#work-with-quantized-types>)
Working with Dynamic Shapes
Deferred dimension specification, optimization profiles, and runtime shape selection.
→ [Working with Dynamic Shapes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-dynamic-shapes.html#work-with-dynamic-shapes>)
Working with Runtime Cache
Persist compiled GPU kernels on disk to reduce JIT startup overhead.
→ [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>)
Working with RTX CUDA Graphs
CUDA Graph capture and replay for lower launch overhead, including dynamic shapes.
→ [Working with RTX CUDA Graphs](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-cuda-graphs.html#work-with-cuda-graphs>)
Simultaneous Compute and Graphics
Run inference alongside graphics workloads (for example, in-game AI features).
→ [Simultaneous Compute and Graphics](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/compute-graphics.html#compute-graphics>)
CPU-Only AOT and TensorRT-RTX Engines
Build portable engines without a GPU and target specific compute capabilities.
→ [CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>)
Porting Guide for TensorRT Applications
Differences between TensorRT and TensorRT-RTX, engine strategies, and API migration notes.
→ [Porting Guide for TensorRT Applications](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>)
TensorRT-RTX API Capture and Replay
Record and replay engine-building API sequences for debugging (Linux).
→ [TensorRT-RTX API Capture and Replay](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/capture-replay.html#capture-replay>)
For ONNX export and installation prerequisites, refer to [ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) and [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) . For performance tuning, see [Best Practices](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/best-practices.html#best-practices>) .
