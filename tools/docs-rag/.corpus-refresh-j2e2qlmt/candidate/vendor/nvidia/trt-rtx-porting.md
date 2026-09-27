Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html

Porting Guide for TensorRT Applications
TensorRT-RTX optimizes the inference performance of your AI models and simplifies deployment of your applications to end-user PCs with NVIDIA RTX GPUs running Windows or Linux. This section walks you through:
Understanding whether TensorRT-RTX is the right inference framework for your application.
How to port an existing TensorRT application to use TensorRT-RTX.
Choosing an Inference Solution
Both TensorRT and TensorRT-RTX excel at optimizing non-LLM models such as CNNs, diffusion models, transformers, and more. At first glance, TensorRT-RTX appears quite similar to TensorRT, since porting TensorRT applications was an explicit design goal. However, there are key differences in their capabilities that make TensorRT-RTX more suitable for certain applications, while TensorRT is better for others.
Use the decision tree below to pick a starting point, then read the product summaries that follow for deployment trade-offs.
Refer to the [Getting Started with TensorRT](<https://developer.nvidia.com/tensorrt#section-get-started-with-tensorrt>) section for a high-level overview of NVIDIA inference solutions. The following sections explore the key differences between TensorRT and TensorRT-RTX in more detail.
NVIDIA TensorRT
[TensorRT](<https://docs.nvidia.com/deeplearning/tensorrt/latest/index.html>) is ideal for running models in automotive and other robotics use cases, particularly when deploying to one or a few specific GPU models such as Jetson Thor or Orin. TensorRT can also be an excellent choice for running certain non-LLM data center workloads when you are deploying to one or a few specific GPUs such as GB200 or H100.
When you build a TensorRT engine for your model, TensorRT performs autotuning directly on the target device. This requires access to a machine with the target GPU model. The resulting engine will then run on that GPU with high performance.
For optimal performance across multiple GPU models, you need to build and deploy an engine for each one. Additionally, you must include the TensorRT runtime libraries, which are quite large (around 1 GB, depending on the OS).
While TensorRT often delivers the best performance, deploying TensorRT-optimized models to Windows and Linux PC users with various NVIDIA GPUs can be challenging.
NVIDIA TensorRT-RTX
To address deployment challenges, TensorRT-RTX:
Uses Just-In-Time (JIT) compilation on the end-user device.
Produces inference engines for any NVIDIA RTX GPU starting from the Turing family, without a per-device build step in your release pipeline.
Is deployed with a library smaller than 200 MB.
These features allow you to deploy the same application, and even the same engine, to end users across a range of NVIDIA RTX GPUs.
When to choose TensorRT : Use TensorRT when targeting only one or very few GPU models, and you need the highest possible inference throughput.
When to choose TensorRT-RTX : Use TensorRT-RTX to deploy applications to end users on Windows and Linux PCs with RTX GPUs, where a smaller runtime and a single portable engine matter more than squeezing out the last increment of throughput on one specific GPU.
Benchmarking
To determine if each solution can meet your performance requirements, compare the performance of TensorRT-RTX and TensorRT. Use the command-line tool provided by each product to assist with the comparison:
Produce a serialized engine for your model. You can either:
Use your own application code and store the result of IBuilder::buildSerializedNetwork() to a file, or
Use the appropriate command-line tool to process an ONNX file
For example:
TensorRT trtexec --onnx = myModel.onnx --saveEngine = myModel.plan
TensorRT-RTX tensorrt_rtx --onnx = myModel.onnx --saveEngine = myModel.rtxplan
Use the command-line tools to measure performance.
TensorRT trtexec --loadEngine = myModel.plan
TensorRT-RTX tensorrt_rtx --loadEngine = myModel.rtxplan
For more information on best practices and flags for performance measurement of TensorRT and TensorRT-RTX, refer to the [Performance Benchmarking with TensorRT Plan File](<https://docs.nvidia.com/deeplearning/tensorrt/latest/performance/best-practices.html#performance-benchmarking-with-tensorrt-plan-file>) section.
Engines
Like TensorRT, TensorRT-RTX requires you to compile your model into an engine before using it for inference. In TensorRT-RTX, these are sometimes called JIT-able engines because they can be Just-In-Time (JIT) compiled on the end-user machine. Throughout the remainder of this document, they are called TensorRT-RTX engines, or engines when the meaning is unambiguous.
TensorRT-RTX engines are produced in TensorRT-RTX when you invoke IBuilder::buildSerializedNetwork() . This Ahead-of-Time (AOT) compilation step typically completes in under 15 seconds. You can save the contents of the resulting IHostBuffer for later use, and optionally exclude weights.
Although TensorRT-RTX engines appear similar to TensorRT engines in the APIs, they are not compatible. When porting your application from TensorRT to TensorRT-RTX, you must build new engines. The following sections discuss your options for building these engines.
Deployment Options
Every model you compile with TensorRT-RTX results in an engine that your application later loads and executes on the end-user machine. You have two major options for when and where you perform AOT compilation, depending on your application’s needs and goals.
CPU-Only AOT
By default, AOT compilation uses only the CPU and produces an engine compatible with all Ampere and later RTX GPUs. This allows you to compile your TensorRT-RTX engine in advance and then include the engine bytes as part of your application download.
If you also want to support Turing GPUs such as the RTX 20 series, create a second engine specifically targeted at [CUDA Compute Capability 7.5](<https://developer.nvidia.com/cuda-gpus>) . Then deploy both engines: one for Ampere and later GPUs, and one for Turing GPUs. For more information, refer to the [CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) and APIs sections.
For release compatibility requirements and lightweight validity checks before deserialization, refer to [Engine Compatibility](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#version-compat>) .
On-Device AOT
TensorRT-RTX engines can typically be compiled in 15 seconds or less. Given this speed, you can perform AOT compilation directly on the end-user’s machine.
For example, you could perform AOT compilation during installation or upon the first run of the application, targeting only the end-user’s specific GPU. You can then save that engine to persistent storage on the user’s machine for later use.
This approach results in a smaller engine and, in some cases, can yield better performance. You should measure the performance of AOT compilation and inference for your model to determine the best deployment approach.
If you choose the On-Device AOT strategy, ensure that your application rebuilds the engine if you update the version of the TensorRT-RTX runtime library. Additionally, check for changes to the user’s installed GPU; if they upgrade to a newer NVIDIA GPU, you will need to rebuild the engine. Refer to [Version Compatibility](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#version-compatibility>) for release compatibility requirements and to [Compatibility Checks](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#compatibility-checks>) for instructions on determining engine compatibility using the [IRuntime::getEngineValidity()](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/_static/c-api/classnvinfer1_1_1_i_runtime.html#ac065f4ac6a37a1a60be100f5129eba3b>) API.
Libraries
TensorRT-RTX requires two libraries:
libtensorrt_rtx.so (Linux) or tensorrt_rtx_1_6.dll (Windows)
The tensorrt_rtx library provides all the core TensorRT-RTX C++ APIs for ahead-of-time compilation, just-in-time compilation, and inference. Every TensorRT-RTX application uses this library. It is analogous to the nvinfer library from TensorRT.
libtensorrt_onnxparser_rtx.so (Linux) or tensorrt_onnxparser_rtx_1_6.dll (Windows)
The ONNX parser library allows you to read ONNX files to produce a network that can be compiled into a TensorRT-RTX engine. TensorRT-RTX applications that use ONNX files will require this library. It is analogous to the nvonnxparser library from TensorRT.
For example, if your application uses a CMake build, update the link libraries in your CMakeLists.txt file as follows:
Before TensorRT target_link_libraries ( helloWorld PRIVATE nvinfer nvonnxparser )
After TensorRT-RTX target_link_libraries ( helloWorld PRIVATE tensorrt_rtx tensorrt_onnxparser_rtx )
Using distinct library names allows you to have TensorRT and TensorRT-RTX installed simultaneously.
Python Module
TensorRT-RTX provides Python bindings under the module name tensorrt_rtx , while TensorRT’s Python module name is tensorrt . Therefore, if you are using TensorRT’s Python modules and want to migrate to TensorRT-RTX, you will need to change the module name that you import.
Before TensorRT import tensorrt as trt
After TensorRT-RTX import tensorrt_rtx as trt
The APIs are mostly compatible, so your existing Python code will generally work. If you prefer to use both TensorRT and TensorRT-RTX in your application, you can import and use both.
import tensorrt as trt import tensorrt_rtx as trtrtx
Migration Checklist
Use this checklist when moving a TensorRT application to TensorRT-RTX. Complete each row before release.
Table 14 TensorRT to TensorRT-RTX migration checklist Step
Before (TensorRT)
After (TensorRT-RTX)
Verify
Choose framework
Fixed GPU fleet or datacenter target
Diverse RTX GPUs on end-user PCs
Choosing an Inference Solution decision tree matches your deployment
Benchmark throughput
trtexec with .plan engine
tensorrt_rtx with .rtxplan engine
Latency and throughput meet requirements on representative RTX hardware
Rebuild engines
Serialized .plan from IBuilder::buildSerializedNetwork()
New .rtxplan or in-memory buffer from TensorRT-RTX builder (engines are not interchangeable)
Inference succeeds; no load errors from mixing engine formats
Link libraries (CMake)
nvinfer , nvonnxparser
tensorrt_rtx , tensorrt_onnxparser_rtx
Application links and loads the RTX shared libraries on target OS
Python imports
import tensorrt as trt
import tensorrt_rtx as trt (or both modules if coexisting)
Builder and runtime calls run without ImportError
API and typing differences
May use deprecated or weakly-typed APIs; custom plugins
Deprecated APIs removed; plugins unsupported; strongly typed networks required
Build succeeds; no plugin layers; see API differences table below and [Work With Quantized Types](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-quantized-types.html#work-with-quantized-types>)
Deployment strategy
Per-GPU engine builds on known hardware
CPU-only or on-device AOT; optional Turing vs Ampere+ engine split
[Version Compatibility](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#version-compatibility>) and GPU-change rebuild policy documented for your app
APIs
Porting your TensorRT application to TensorRT-RTX is direct when it suits your needs. The C++ and Python APIs in TensorRT-RTX are almost identical to those in TensorRT, so your application will likely work with only the preceding library or module changes.
The following table summarizes API differences you should plan for during migration:
Table 15 TensorRT vs TensorRT-RTX API differences Difference
What changed
Migration path
Deprecated APIs
Nearly all APIs deprecated in TensorRT have been removed in TensorRT-RTX, including weak-typing APIs removed in TensorRT-RTX 1.5 (previously retained but unsupported)
Consult the [TensorRT API documentation](<https://docs.nvidia.com/deeplearning/tensorrt/latest/api/c-api.html>) for replacement APIs; for typing and quantization, refer to [Work With Quantized Types](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-quantized-types.html#work-with-quantized-types>)
Custom plugins
Plugin layers are not supported; some plugin-related headers remain for compile compatibility only
Remove or replace plugin layers with supported native ops or ONNX graph changes before building the RTX engine
End-user optimization APIs
New APIs target JIT deployment on RTX PCs (CPU-only AOT, runtime cache, dynamic shapes, CUDA graphs)
Adopt as needed: [CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) , [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) , [Working with Dynamic Shapes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-dynamic-shapes.html#work-with-dynamic-shapes>) , [Working with CUDA Graphs](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-cuda-graphs.html#work-with-cuda-graphs>)
For strongly typed networks, explicit quantization, ModelOpt workflows, and porting away from weak typing, refer to [Work With Quantized Types](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-quantized-types.html#work-with-quantized-types>) .
