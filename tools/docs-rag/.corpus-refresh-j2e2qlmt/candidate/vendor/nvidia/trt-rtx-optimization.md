Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/optimization.html

Optimizing TensorRT-RTX Performance
The following sections focus on the general inference flow on GPUs and some general strategies to improve performance on RTX hardware.
What You Will Learn
How network precision , quantization, and the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) interact on RTX GPUs
How dynamic shape specialization and runtime caching reduce JIT overhead at deployment
How batching , within-inference multi-streaming , and CUDA graphs improve throughput
When a workload is enqueue-bound and how to enable CUDA graph capture in TensorRT-RTX
See also
[Performance Benchmarking](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/benchmarking.html#performance-benchmarking>)
Establish a measurement baseline before applying these optimizations so you can quantify the impact of each change.
Table 19 Key optimization recommendations Topic
Recommendation
More detail
Network precision
Prefer lower precisions supported on your GPU; quantize in ONNX where possible
Network Precision below; [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>)
Dynamic shapes
Warm up or use kEAGER specialization; persist kernels with runtime cache
Dynamic Shape Specialization ; [Working with Dynamic Shapes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-dynamic-shapes.html#work-with-dynamic-shapes>)
Runtime caching
Serialize and load runtime cache at startup to skip JIT on known shapes
Runtime Caching ; [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>)
Batching
Sweep batch sizes; on Ada+ smaller batches may improve L2 cache throughput
Batching below
Multi-streaming
Set setMaxAuxStreams() when layers under-utilize the GPU
Within-Inference Multi-Streaming below
CUDA Graphs
Use when Enqueue Time ≈ GPU Compute Time (enqueue-bound workloads)
Inference with CUDA Graphs ; [Working with RTX CUDA Graphs](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-cuda-graphs.html#work-with-cuda-graphs>)
Zero-copy I/O
Use cudaMallocHost() for mapped I/O buffers on integrated GPUs
Zero-Copy I/O for Unified Memory below
Network Precision
Note
Performance optimizations in this release prioritize 16-bit floating-point types. Models that primarily use 16-bit floating-point types frequently achieve throughput comparable to TensorRT. Models that rely heavily on 32-bit floating-point types also benefit from TensorRT-RTX optimizations, but typically do not match TensorRT throughput. Future TensorRT-RTX releases are expected to improve performance across additional models and data types.
TensorRT-RTX networks are strongly typed, meaning that operations will execute at the precision specified in the source model. Inference will generally perform best at lower precisions. Whenever possible, use lower precisions to take advantage of improved performance, keeping in mind that hardware-specific datatype support varies by GPU architecture. Refer to the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) GPU Architecture and Precision Support table for the canonical per-architecture mapping.
To enjoy the additional performance benefit of lower-precision types via quantization, Quantize/Dequantize operations need to be inserted into the ONNX model to tell TensorRT-RTX where to quantize/dequantize the tensors and what scaling factors to use. Quantized models can be created using tools like the TensorRT Model Optimizer or other third-party quantization libraries.
For more information, refer to the [Working with Quantized Types](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-quantized-types.html#quantized-types>) section.
Dynamic Shape Specialization
TensorRT-RTX supports networks with dynamic shapes. Dynamic shapes allow applications to defer specifying some or all tensor dimensions, for network inputs and outputs, until runtime.
A key advantage of the dynamic shapes implementation in TensorRT-RTX is that shape-specialized kernels are generated and compiled based on shapes observed at runtime. In the default lazy mode, inference initially uses fallback kernels while specialized kernels compile in the background. TensorRT-RTX automatically uses the specialized kernels when they become available, so performance can improve over the first iterations for a new shape.
Warm up each shape before measuring steady-state performance. To avoid running fallback kernels when benchmarking, use eager specialization ( tensorrt_rtx --specializeStrategyDS="eager" ), which waits for specialized kernels to be fully compiled before running inference. A populated runtime cache avoids running fallback kernels for previously seen shapes.
For more information, refer to the [Working with Dynamic Shapes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-dynamic-shapes.html#work-with-dynamic-shapes>) and [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) sections.
Runtime Caching
At runtime, TensorRT-RTX compiles hardware-specific kernels using Just-In-Time (JIT) compilation. Runtime caching stores the compiled kernels, allowing future invocations to avoid repeated recompilation. This reduces application startup cost and enables peak performance out-of-the-box.
Attach a runtime cache through the Runtime Config API, serialize it for persistence across application invocations, and load it at startup to skip JIT recompilation for known shapes. For setup code, load/save patterns, and compatibility requirements, refer to [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) .
The following example demonstrates the usage of dynamic shapes with runtime cache for performance benchmarking with tensorrt_rtx :
# Build a dynamic-shape engine.
tensorrt_rtx --onnx=resnet50-v1-12.onnx \
--minShapes=data:1x3x224x224 \
--optShapes=data:4x3x224x224 \
--maxShapes=data:16x3x224x224 \
--saveEngine=resnet50-v1-12.plan \
--skipInference
# Run inference with batch 4 and save the artifacts to cache.
tensorrt_rtx --loadEngine=resnet50-v1-12.plan \
--shapes=data:4x3x224x224 \
--specializeStrategyDS=eager \
--runtimeCacheFile=resnet50.runtime.cache
# On a later run, load the same cache for reduced startup and specialization time.
tensorrt_rtx --loadEngine=resnet50-v1-12.plan \
--shapes=data:4x3x224x224 \
--runtimeCacheFile=resnet50.runtime.cache
Batching
In TensorRT-RTX, a batch is a collection of inputs that can all be processed uniformly. Each instance in the batch has the same shape and flows through the network similarly. Therefore, each instance can be trivially computed in parallel.
Each network layer will have some overhead and synchronization required to compute forward inference. By computing more results in parallel, this overhead is paid off more efficiently. In addition, many layers are performance-limited by the smallest dimension in the input. If the batch size is one or small, this size can often be the performance-limiting dimension.
On NVIDIA Ada Lovelace or later GPUs, decreasing the batch size may improve the throughput significantly if the smaller batch sizes help the GPU cache the input/output values in the L2 cache. Therefore, various batch sizes should be tried to find the batch size that provides optimal performance.
Within-Inference Multi-Streaming
TensorRT-RTX can run independent layers concurrently on auxiliary CUDA streams. This can improve performance when individual layers do not fully utilize the GPU.
Set the maximum number of auxiliary streams during engine building. For example, to allow up to eight streams - one main stream and seven auxiliary streams, use the tensorrt_rtx command with the --maxAuxStreams flag:
tensorrt_rtx --onnx=resnet50-v1-12.onnx \
--shapes=data:4x3x224x224 \
--maxAuxStreams=7
TensorRT-RTX may use fewer streams when additional concurrency would not help and it manages synchronization between the main and auxiliary streams automatically. Since auxiliary streams can increase activation-memory usage, benchmark performance and memory consumption when enabling them. For more information on multi-streaming within inference, refer to [Within-Inference Multi-Streaming](<https://docs.nvidia.com/deeplearning/tensorrt/latest/performance/optimization.html?highlight=setAuxStreams#within-inference-multi-streaming>) .
Inference with CUDA Graphs
CUDA Graphs represent a sequence (or, more generally, a graph) of kernels in a way that allows CUDA to optimize their scheduling. This can be particularly useful when enqueue and launch times take longer than the actual GPU executions, causing the latency of enqueueV3() calls to become the performance bottleneck. This type of workload is called enqueue-bound .
This can occur if the workload is very small in terms of the number of computations, such as containing convolutions with small I/O sizes, matrix multiplications with small GEMM sizes, or mostly element-wise operations throughout the network, then the workload tends to be enqueue-bound. This is because most CUDA kernels take the CPU and the driver around 5-15 microseconds to launch per kernel, so if each CUDA kernel execution time is only a few microseconds long on average, the kernel launch time becomes a primary performance bottleneck.
With tensorrt_rtx , you can tell that a workload is enqueue-bound if the reported Enqueue Time is close to or longer than the reported GPU Compute Time . To reduce this overhead, you can increase the computations in each CUDA kernel by increasing the batch size, and you can use TensorRT-RTX’s built-in CUDA Graphs to capture and replay the sequence of kernel launches for you.
Using CUDA Graphs with TensorRT-RTX Execution Context
TensorRT-RTX provides built-in CUDA graph support via IRuntimeConfig::setCudaGraphStrategy() . Enable whole-graph capture when Enqueue Time approaches GPU Compute Time (see above). For the full setup walkthrough with C++ and Python examples, refer to [Working with RTX CUDA Graphs](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-cuda-graphs.html#work-with-cuda-graphs>) .
TensorRT-RTX built-in CUDA graphs provide automatic support for dynamic shapes, handling capture and replay in the presence of both fallback and specialized kernels, as detailed in the [Working with Dynamic Shapes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-dynamic-shapes.html#work-with-dynamic-shapes>) section.
Limitations of CUDA Graphs
CUDA graphs cannot handle some operations, so graph capture may fail if the execution context contains such operations. Typical deep learning operators unsupported by CUDA graphs include loops, conditionals, and layers requiring data-dependent shapes. In these cases, TensorRT-RTX will fall back to the default execution path. Refer to the [CUDA Programming Guide](<https://docs.nvidia.com/cuda/cuda-c-programming-guide/index.html#prohibited-and-unhandled-operations>) to learn more about the limitations of CUDA graphs.
Graphs captured with TensorRT-RTX are specific to the input size and the locations of input and output buffers. Modifying these parameters will result in automatic graph recapture. In particular, if the application is managing memory for activations using create_execution_context(tensorrt_rtx.ExecutionContextAllocationStrategy(2)) , the memory address is also captured as part of the graph.
Therefore, best practice is to use one execution context per captured graph and to share memory across the contexts with create_execution_context(tensorrt_rtx.ExecutionContextAllocationStrategy(2)) .
Concurrent CUDA Activities with CUDA Graph Capture
Launching a CUDA kernel on the CUDA legacy default stream or calling synchronous CUDA APIs like cudaMemcpy() while capturing a CUDA graph fails because these CUDA activities implicitly synchronize the CUDA streams used by TensorRT-RTX execution contexts.
To avoid breaking the CUDA graph capture, ensure other CUDA kernels or stream captures are launched on non-default CUDA streams and use the asynchronous version of CUDA APIs, like cudaMemcpyAsync() .
For additional information, refer to the [Working with RTX CUDA Graphs](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-cuda-graphs.html#work-with-cuda-graphs>) section.
Zero-Copy I/O for Unified Memory
On systems with an integrated GPU, such as NVIDIA RTX Spark and NVIDIA DGX Spark, the CPU and GPU can access the same physical memory. Applications that ordinarily copy inputs from host to device before inference and outputs from device to host afterward can reduce this transfer overhead by using mapped host memory for I/O buffers.
Query CU_DEVICE_ATTRIBUTE_INTEGRATED with cuDeviceGetAttribute() to determine whether the active GPU is integrated. If the attribute is nonzero, allocate input and output buffers with cudaMallocHost() . These page-locked allocations are mapped so that the CPU and GPU can access them directly without explicit host-to-device or device-to-host copies.
Do not use cudaMallocManaged() for this I/O path. Managed memory uses different allocation and migration behavior that can reduce read and write performance for this workload. On discrete-GPU systems, mapped host-memory access crosses the CPU-GPU interconnect and is generally slower than device-local memory.
Note
TensorRT-RTX continues to allocate internal scratch memory in device memory. Mapped I/O buffers can provide lower memory bandwidth than device-local allocations, but eliminating explicit input and output copies is generally beneficial on integrated-GPU systems.
