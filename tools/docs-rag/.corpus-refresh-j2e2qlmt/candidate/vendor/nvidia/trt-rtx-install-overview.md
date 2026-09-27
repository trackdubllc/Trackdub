Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installation-overview.html

Installation Guide Overview
This guide provides complete instructions for installing and deploying TensorRT-RTX on supported platforms. Whether you are setting up TensorRT-RTX for the first time or upgrading an existing installation, this guide walks you through the process.
About TensorRT-RTX
NVIDIA TensorRT for RTX (TensorRT-RTX) is an inference library that brings TensorRT optimization to NVIDIA RTX GPUs across desktops, laptops, and workstations. It uses a portable ahead-of-time (AOT) engine build plus just-in-time (JIT) compilation on the end-user GPU, so you do not need a separate full engine build for every target device.
For the two-phase compilation pipeline and ecosystem context, refer to the [Architecture Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/architecture-overview.html#architecture-overview>) . For runtime object lifetimes, threading, and memory contracts when embedding TensorRT-RTX, refer to [How TensorRT-RTX Works](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#works>) .
Recommended First-Time Path
Follow these steps in order. Each step links to the right topic; the sidebar section name only tells you where that page lives in the docs, not whether you must stay in one part of the guide.
1. Install ( Installing TensorRT-RTX )
Check prerequisites : Verify supported hardware, OS, and dependencies → [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>)
Install the SDK : Download and set up TensorRT-RTX on your platform → [Installing TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html#installing>)
2. Verify and plan deployment ( Getting Started )
Verify your install : Run the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) (download a sample ONNX model, build an engine, run inference on your RTX GPU)
Plan deployment : After Quick Start succeeds, use [Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) for bundling engines, portability, and runtime-cache storage
3. Your models and application (sidebar: Installing TensorRT-RTX and Inference Library )
Export or integrate models : [ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) or [Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>)
Integrate in your app : [Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>)
