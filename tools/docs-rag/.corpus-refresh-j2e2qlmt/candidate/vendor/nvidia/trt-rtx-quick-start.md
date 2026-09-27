Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html

Quick Start Guide
This guide walks you from a sample ONNX model to a running TensorRT-RTX engine on your RTX GPU using the tensorrt_rtx command-line tool in three steps: download a model, build a portable engine ( AOT , ahead-of-time), and run inference ( JIT , just-in-time). For how those phases fit together, refer to the [Architecture Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/architecture-overview.html#architecture-overview>) .
By the end you will have:
An ONNX model on disk
A serialized resnet_engine.trt engine file
A successful inference run and a runtime cache for faster later launches
Note
GPU required only for Step 3. Steps 1 and 2 run on a CPU-only machine (useful for build servers). Step 3 needs an installed RTX GPU and drivers that match [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) .
Prerequisites
Before you begin:
Install TensorRT-RTX. Refer to [Installation Guide Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installation-overview.html#installation-overview>) and [Installing TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html#installing>) .
Confirm your platform and GPU against [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) and the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) .
Confirm the CLI is on your PATH : tensorrt_rtx --help (Linux) or tensorrt_rtx.exe --help (Windows). If that fails, skip to If a Command Fails below.
Step 1: Download a Sample Model
This walkthrough uses a pre-trained ResNet-50 model from the [ONNX Model Zoo](<https://github.com/onnx/models>) :
Linux wget https://download.onnxruntime.ai/onnx/models/resnet50.tar.gz
tar xzf resnet50.tar.gz
Windows Invoke-WebRequest -Uri https :// download . onnxruntime . ai / onnx / models / resnet50 . tar . gz -OutFile resnet50 . tar . gz tar -xzf resnet50 . tar . gz
Listing 1 Expected output (paths may vary)
resnet50/model.onnx
You should now have resnet50/model.onnx . To use your own model instead, export it to ONNX first; refer to the [ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) .
Step 2: Build the Engine (AOT)
Convert the ONNX model into a TensorRT-RTX engine. This step does not require a GPU and typically takes 20–30 seconds for most models.
Linux tensorrt_rtx --onnx = resnet50/model.onnx --saveEngine = resnet_engine.trt
Windows tensorrt_rtx . exe - -onnx = resnet50 \ model . onnx - -saveEngine = resnet_engine . trt
Listing 2 Expected output (representative)
Engine serialized to resnet_engine.trt
Engine size: ~25 MB (varies by model and precision)
You should see resnet_engine.trt in your working directory. This file is portable across supported RTX GPUs and operating systems.
Step 3: Run Inference (JIT)
Load the engine and run inference on your RTX GPU. The first run JIT-compiles kernels for your device; --runtimeCacheFile saves that work for later launches.
Linux tensorrt_rtx --loadEngine = resnet_engine.trt \ --runtimeCacheFile = resnet.cache
Windows tensorrt_rtx . exe - -loadEngine = resnet_engine . trt ` - -runtimeCacheFile = resnet . cache
Listing 3 Expected output (representative; first run includes JIT compile time)
Inference latency: ~3 ms per iteration (GPU-dependent)
Runtime cache written to resnet.cache
If inference completes with latency output and a cache file is written, your TensorRT-RTX setup is working end to end.
What You Just Did
You verified three things in three commands:
TensorRT-RTX can read an ONNX model and compile it to a portable engine (AOT).
The runtime can load that engine on your RTX GPU and run inference (JIT).
Runtime caching can persist JIT artifacts for faster subsequent runs.
This is the minimum viable deployment loop. [Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) expands this walkthrough with production notes; the sections below point to deeper topics.
Artifacts checklist (you are ready for deployment topics when all are present):
resnet50/model.onnx (or your own ONNX file)
resnet_engine.trt from Step 2
resnet.cache from Step 3 (after a successful JIT run)
If a Command Fails
Confirm the CLI is installed and on your PATH :
Linux tensorrt_rtx --help
Windows tensorrt_rtx . exe - -help
If that fails, return to [Installing TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html#installing>) and recheck PATH (Windows) or PATH / LD_LIBRARY_PATH (Linux) settings.
Symptom
What to check
--help not found or unknown command
SDK/tarball bin on PATH ; on Linux, lib on LD_LIBRARY_PATH . Re-run install steps in [Installing TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html#installing>) .
Step 1: archive missing or no resnet50/model.onnx
Re-download and extract; confirm your working directory matches the paths in Step 2.
Step 2: build errors or no resnet_engine.trt
No GPU is required. Check ONNX path, unsupported operators ( [Operators](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/operators.html#operators>) ), and [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) precision limits.
Step 3: no GPU, driver, or CUDA errors
RTX GPU present, drivers installed, CUDA toolkit version matches prerequisites. The first JIT run can take several seconds; that is normal.
Step 3: inference runs but no cache file
Confirm --runtimeCacheFile is set and the process can write to the output directory.
Other Workflows
Most applications stay on the ONNX + tensorrt_rtx CLI path above. When you outgrow it:
Your own ONNX models : [ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>)
C++ or Python integration : [Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>)
PyTorch : [Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>)
Port from TensorRT : [Porting Guide for TensorRT Applications](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>)
Performance tuning : [Best Practices](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/best-practices.html#best-practices>)
Run tensorrt_rtx --help for the full CLI flag reference.
