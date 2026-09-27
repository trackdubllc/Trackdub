Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/release-notes-1/1.6.html

TensorRT-RTX 1.6 Release Notes
These Release Notes apply to Linux and Windows users. This release will include several fixes from the previous TensorRT-RTX releases and additional changes.
Announcements
Windows on ARM support for NVIDIA RTX Spark : TensorRT-RTX 1.6 adds
official Windows on ARM support for NVIDIA RTX Spark, extending
TensorRT-RTX inference deployment to supported ARM-based Windows systems.
Learn more about [NVIDIA RTX Spark](<https://www.nvidia.com/en-us/products/rtx-spark/>) .
Linux SBSA support for NVIDIA DGX Spark : TensorRT-RTX 1.6 adds official
Linux SBSA support for NVIDIA DGX Spark, replacing the experimental support
introduced in TensorRT-RTX 1.5 with support for compatible ARM64 Linux
environments.
Runtime cache compatibility tightened : TensorRT-RTX 1.6 strengthens
runtime cache compatibility checks. The runtime environment must match the
cached GPU SKU, TensorRT-RTX version, and Compute-in-Graphics (CiG) state,
and the driver version must be greater than or equal to the cached version.
Refer to [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) .
Key Features and Enhancements
Inference and Deployment
CUDA 13.4 support : Added support for NVIDIA CUDA Toolkit 13.4, with continued support for CUDA 12.9 Update 1. Separate TensorRT-RTX packages are provided for each supported CUDA version.
Zero-copy I/O for unified memory : On NVIDIA RTX Spark and NVIDIA DGX Spark, applications can allocate mapped input and output buffers with cudaMallocHost() to avoid explicit host-to-device and device-to-host copies. Refer to [Zero-Copy I/O for Unified Memory](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/optimization.html#zero-copy-io-integrated-gpus>) .
Deferred weights loading : Added [deferred weights loading](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#deferred-weights-loading>) to deserialize engines and JIT-compile kernels without allocating engine weights in host or GPU memory. Applications can optionally persist the compiled kernels in a runtime cache and load weights directly from storage to the GPU before inference. Deferred weights loading and weight streaming cannot be enabled at the same time.
Documentation
Getting Started reorganized : Added [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) and moved [Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) into Getting Started alongside release notes and the support matrix.
Support matrix consolidated and made interactive : Consolidated support information into a single [TensorRT-RTX Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) page and added interactive explorers for software and system requirements and for GPU architecture and precision support.
Architecture chapter expanded : Added [How TensorRT-RTX Works](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#works>) for runtime lifetimes, threading, memory, and engine compatibility, and restructured [Architecture Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/architecture-overview.html#architecture-overview>) to match the TensorRT enterprise layout.
Performance and Advanced Topics restructured : Split [Best Practices](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/best-practices.html#best-practices>) into benchmarking and optimization guides, and expanded [Advanced Topics](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/advanced.html#advanced>) with standalone engine compatibility, refitting, timing-cache migration, and weight-streaming pages.
Runtime tutorials modernized : Updated native API walkthroughs to setTensorAddress / enqueueV3 (C++) and set_tensor_address / execute_async_v3 (Python). Refer to [Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>) .
Compatibility
For comprehensive platform compatibility, hardware requirements, and feature availability information, refer to the [TensorRT-RTX Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) . The support matrix provides detailed information about supported operating systems, CUDA versions, GPU architectures, precision modes, compiler requirements, and ONNX operator support for TensorRT-RTX 1.6.
Limitations
Using a timing cache through ITimingCache and related APIs forces the ahead-of-time compilation step to query your system for a GPU (that is, prevents use of CPU-only ahead-of-time compilation). This has been true since the initial release (version 1.0). The timing cache has no effect on the built engine in TensorRT-RTX. The timing cache APIs were deprecated in 1.2 and will be removed in a future update. They remain in place to avoid breaking source or binary compatibility. Applications should stop using timing cache APIs in preparation for their removal.
If the cache size grows too large (larger than 100 MB), it might require more overhead to serialize and deserialize from disk. If it negatively affects performance, delete the cache file and recreate one.
TensorRT-RTX engines are not forward-compatible with other versions of the TensorRT-RTX runtime. Ensure that any TensorRT-RTX engines you produce are run using the runtime from the same version of TensorRT-RTX that was used to generate the engine.
While TensorRT-RTX supports Turing ( [CUDA compute capability 7.5](<https://developer.nvidia.com/cuda-gpus>) ), a TensorRT-RTX engine created with default Compute Capability settings will produce an engine with support for Ampere and later GPUs, therefore excluding Turing. It is recommended to build a separate engine specifically for Turing to achieve the best performance. Creating a single engine with support for Turing and later GPUs will lead to less performant inference on the Ampere and later GPUs due to technical limitations of the engine format.
Deprecated API Lifetime
APIs deprecated in TensorRT-RTX 1.6 will be retained until 7/2027.
APIs deprecated in TensorRT-RTX 1.5 will be retained until 5/2027.
APIs deprecated in TensorRT-RTX 1.4 will be retained until 3/2027.
APIs deprecated in TensorRT-RTX 1.3 will be retained until 12/2026.
APIs deprecated in TensorRT-RTX 1.2 will be retained until 10/2026.
APIs deprecated in TensorRT-RTX 1.1 will be retained until 8/2026.
Fixed Issues
Fixed an issue that could cause ICudaEngine::createExecutionContext() to crash during JIT initialization when a model used FP8 convolutions with certain dynamic input shapes.
Fixed a Windows race condition that could occur when multiple threads ran inference concurrently across multiple engines.
Improved the performance of convolutions with large filter sizes.
Fixed an issue that could cause the default TensorRT-RTX asynchronous CUDA allocator to fail unexpectedly on Windows.
Reduced GPU memory usage for static-shape graphs.
Improved the performance of some deconvolution nodes at large resolutions.
Improved the performance of some models that use GridSample nodes.
Fixed an issue that prevented built-in CUDA Graphs from automatically deactivating when weight streaming was in use.
Improved runtime-cache deserialization latency by approximately 20–30%.
Improved the performance of certain diffusion models through fusion optimizations.
Improved the performance of instance normalization layers.
Fixed an issue that could cause inference to hang when multiple execution contexts ran concurrently on the same GPU with CUDA Graphs enabled. The hang was caused by contention during simultaneous CUDA Graph capture. Capture is now serialized across contexts. The issue affected all GPU architectures.
Fixed an issue that could cause cuDNN graph compilation to fail with Error Code 1: Myelin on the first inference call for dynamic-shape ONNX models that used ceil-mode average pooling ( AveragePool with ceil_mode=1 ). The engine built successfully but failed during execution. The issue affected all GPU architectures.
Reduced CPU overhead through inference-pipeline optimizations.
Fixed an issue that caused TensorRT-RTX to ignore the specified matrix-multiplication accumulation data type.
Fixed an accuracy issue for deconvolution layers.
Improved the performance of group normalization layers on devices with more streaming multiprocessors.
Fixed an issue with the --exportLayerInfo command-line option for the tensorrt_rtx executable.
Fixed an issue that could cause device conflicts when using TensorRT-RTX with PyCUDA in Python. For compatibility, continue to use import pycuda.autoprimaryctx instead of import pycuda.autoinit .
Fixed an issue that could cause builds to fail when a MatrixMultiply , Convolution , or Deconvolution op used FP32 inputs and outputs and was followed by a cast to a lower-precision type (for example, FP16 or FP8). The issue affected all GPU architectures.
Known Issues
Functional
NonMaxSuppression , NonZero , and Multinomial layers are not supported.
Only the WDDM driver for Windows is supported. The TCC driver for Windows (refer to [Tesla Compute Cluster (TCC)](<https://docs.nvidia.com/gameworks/content/developertools/desktop/tesla_compute_cluster.htm>) ) is unsupported and may fail with the following error.
[E] Error[1]: [defaultAllocator.cpp::nvinfer1::internal::DefaultAllocator::allocateAsync::48] Error Code 1: Cuda Runtime (operation not supported)
For instructions on changing the driver mode, refer to the [Nsight Visual Studio Edition documentation](<https://docs.nvidia.com/nsight-visual-studio-edition/reference/index.html#setting-tcc-mode-for-tesla-products>) .
By default, TensorRT-RTX uses cudaMallocAsync() for GPU memory allocation. In some environments, such as CUDA in Graphics (CiG) mode or unified-memory systems such as NVIDIA RTX Spark, allocations can fail even when physical GPU memory remains available. Upgrading to the latest CUDA driver can reduce the likelihood of this issue. To avoid these allocator-specific failures, provide a custom IGpuAllocator implementation backed by cudaMalloc() . Refer to the [synchronous GPU allocator sample](<https://github.com/NVIDIA/TensorRT-RTX/tree/main/samples/memoryManagement/>) for an example. This workaround can slightly reduce performance and disables CUDA Graph support.
Convolutions and deconvolutions with both non-unit strides and dilations are not supported for all precisions. Non-unit strided convolutions and deconvolutions, and non-unit dilated convolutions and deconvolutions are supported.
On Windows, the following symbols in tensorrt_rtx_1_6.dll should not be used and will be removed in the future:
?disableInternalBuildFlags@nvinfer1@@YAXAEAVINetworkDefinition@1@_K@Z
?enableInternalBuildFlags@nvinfer1@@YAXAEAVINetworkDefinition@1@_K@Z
?getInternalBuildFlags@nvinfer1@@YA_KAEBVINetworkDefinition@1@@Z
?setDebugOutput@nvinfer1@@YAXAEAVIExecutionContext@1@PEAVIDebugOutput@1@@Z
?setInternalBuildFlags@nvinfer1@@YAXAEAVINetworkDefinition@1@_K@Z
nvinfer1DisableInternalBuildFlags
nvinfer1EnableInternalBuildFlags
nvinfer1GetInternalBuildFlags
nvinfer1SetInternalBuildFlags
Performance
Use of the CPU-only ahead-of-time (AOT) feature can lead to reduced performance for some models. Affected applications achieve the best performance if they instead perform ahead-of-time compilation on-device, targeted to the specific end-user machine. This can be done with the --useGPU flag for the tensorrt_rtx binary, or when using the APIs by setting the compute capabilities to contain only kCURRENT using IBuilderConfig::setComputeCapability() . Measure performance with both approaches to determine which is best for your application. This performance discrepancy is targeted for resolution in a future release.
On Windows, TensorRT-RTX 1.6 might provide reduced performance for models that contain certain concatenation followed by convolution patterns.
