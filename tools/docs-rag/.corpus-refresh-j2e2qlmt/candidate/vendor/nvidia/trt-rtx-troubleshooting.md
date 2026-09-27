Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/troubleshooting.html

Troubleshooting
This guide helps you diagnose and resolve common issues when installing, building engines with, and running inference using TensorRT-RTX on RTX GPUs. It points to the most relevant documentation for platform support, deployment, and debugging workflows.
What You Will Learn
Installation and environment issues : GPU, driver, CUDA, and SDK setup problems on supported platforms
Build and engine issues : AOT/JIT compilation failures, engine compatibility, and porting from TensorRT
Inference and runtime issues : Execution errors, dynamic shapes, and reproducing failures with capture and replay
How to Use This Guide
Confirm platform support : Verify your GPU, OS, and dependency versions against the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) and [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) before changing application code.
Match the workflow phase : Use the installation, build, or inference sections below depending on where the failure occurs.
Reproduce build failures : On Linux, use [API Capture and Replay](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/capture-replay.html#capture-replay>) to record and replay engine-building API sequences without the original application or model source.
Gather diagnostics : Capture TensorRT-RTX version, GPU model, driver and CUDA versions, and relevant log output before reporting an issue.
Getting Additional Help
If this guide does not resolve your issue:
Post questions on the [NVIDIA Developer Forum](<https://forums.developer.nvidia.com/c/ai-data-science/deep-learning/tensorrt/>) with the tensorrt tag
Open an issue on the [TensorRT-RTX GitHub repository](<https://github.com/NVIDIA/TensorRT-RTX/issues>)
Review [Release Notes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/release-notes.html#release-notes>) for known issues and fixes in your version
Installation Issues
Symptoms such as missing libraries, loader errors, or the SDK failing to detect a GPU often trace to environment setup rather than application logic.
Check supported hardware and software
Confirm that your RTX GPU, operating system, driver, and CUDA toolkit versions are in scope for your TensorRT-RTX release.
→ [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) → [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>)
Complete a clean install
Follow the platform-specific install steps for the SDK zip (Windows) or tarball (Linux), including PATH and library path configuration.
→ [Installing TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html#installing>) → [Installation Guide Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installation-overview.html#installation-overview>)
Python package import failures
If import tensorrt_rtx fails after install, verify that the wheel or bindings match your Python version and that dependent CUDA libraries are on the library path. Refer to [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) for dependency versions.
Build and Engine Issues
Engine build failures occur during ahead-of-time (AOT) compilation on the build machine or during just-in-time (JIT) compilation when the engine is first loaded on the end-user GPU.
Unsupported ONNX operators or graph features
Compare your model against the ONNX operator support list and export guidance. Unsupported ops or deprecated network modes (for example, weakly typed or implicit quantization networks) must be updated before build.
→ [Operators](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/operators.html#operators>) → [ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) → [Porting Guide for TensorRT Applications](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>)
TensorRT engine is not compatible with TensorRT-RTX
Serialized TensorRT engines cannot be loaded by TensorRT-RTX. Rebuild engines with TensorRT-RTX and update library and Python module names as described in the porting guide.
→ [Porting Guide for TensorRT Applications](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>)
Engine compatibility across GPUs and releases
Engines built for one compute capability or TensorRT-RTX version may not deserialize or run on another. Use CPU-only AOT defaults, Turing-specific engines where needed, and IRuntime::getEngineValidity() before deployment.
→ [CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) → [Version Compatibility](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#version-compatibility>)
Reproduce build-time failures (Linux)
Record API calls during the build phase and replay them with tensorrt_player to isolate builder failures without the original application.
→ [TensorRT-RTX API Capture and Replay](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/capture-replay.html#capture-replay>)
Inference and Runtime Issues
Failures during engine deserialization, JIT compilation, or execution often relate to engine validity, tensor shapes, or concurrent GPU use.
Engine fails to load or JIT compile on the target machine
Verify GPU support, driver version, and that the engine was built for the installed TensorRT-RTX release. Check release notes for fixed issues affecting your GPU architecture.
→ [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) → [Release Notes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/release-notes.html#release-notes>) → [CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>)
Shape, precision, or accuracy errors at runtime
Dynamic input dimensions, quantized types, and strongly typed networks have specific API and ONNX requirements.
→ [Working with Dynamic Shapes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-dynamic-shapes.html#work-with-dynamic-shapes>) → [Work With Quantized Types](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-quantized-types.html#work-with-quantized-types>) → [Advanced Topics](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/advanced.html#advanced>)
Performance or concurrent graphics workloads
For latency tuning and simultaneous compute and graphics (SCG) scenarios, refer to performance and CiG documentation.
→ [Best Practices](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/best-practices.html#best-practices>) → [Simultaneous Compute and Graphics](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/compute-graphics.html#compute-graphics>)
Debugging with capture and replay
When inference or rebuild behavior differs between machines, capture the build-phase API trace on a system where the issue reproduces and replay on another host.
→ [TensorRT-RTX API Capture and Replay](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/capture-replay.html#capture-replay>)
