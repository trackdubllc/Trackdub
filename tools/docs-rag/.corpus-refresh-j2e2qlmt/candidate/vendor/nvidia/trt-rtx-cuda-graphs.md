Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-cuda-graphs.html

Working with RTX CUDA Graphs
TensorRT-RTX provides out-of-the-box CUDA Graph support, enabling CUDA Graph optimizations with a single line of code change and natively supporting intelligent dynamic shape graph capture.
Introduction
The NVIDIA CUDA Toolkit includes support for [CUDA Graphs](<https://docs.nvidia.com/cuda/cuda-runtime-api/group__CUDART__GRAPH.html>) , which enables the capture and construction of execution graphs that optimize the GPU operation launch workflow. CUDA Graphs help reduce runtime overhead by minimizing CPU-to-GPU kernel launch overhead and lowering GPU grid initialization costs.
You can implement CUDA Graphs in your TensorRT inference workflows by capturing the stream during inference. However, with [TensorRT-RTX using dynamic shapes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-dynamic-shapes.html#work-with-dynamic-shapes>) , TensorRT-RTX compiles and runs two types of dynamic shape kernels: fallback kernels and shape-specialized kernels. This can make it difficult to determine the optimal points to begin and end CUDA Graph capture.
To address this, TensorRT-RTX now provides a native API that requires only a single line of code change to enable intelligent CUDA Graph capture for dynamic shapes. This API allows more precise and effective graph management, even in the presence of runtime shape variation.
This API provides the most performance improvements in the following scenarios:
Inference with dynamic input shapes
Windows operating systems with hardware-accelerated GPU scheduling
Note
RTX CUDA Graphs skips capture on the first iteration of inference to allow necessary set up and global driver calls. It will automatically capture and replay the graph starting on the second inference. Subsequent inferences reuse the captured graph where applicable.
APIs
This RTX CUDA Graphs feature is introduced as part of the runtime config ( IRuntimeConfig ), which is in turn used for execution context creation. Ensure the application has completed the necessary steps up to deserializing the TensorRT-RTX engine. The deserialized engine should be available to create an execution context.
This RTX CUDA Graphs feature is disabled by default. The following flow demonstrates how to enable it:
Create a runtimeConfig object from the engine.
C++ 1 IRuntimeConfig * runtimeConfig = engine -> createRuntimeConfig ();
Python 1 runtime_config = engine . create_runtime_config ()
Set the CUDA Graph strategy to whole graph capture and confirm the setting.
C++ 1 bool success = runtimeConfig -> setCudaGraphStrategy ( nvinfer1 :: CudaGraphStrategy :: kWHOLE_GRAPH_CAPTURE ); 2 3 assert ( success );
Python 1 runtime_config . cuda_graph_strategy = trt . CudaGraphStrategy . WHOLE_GRAPH_CAPTURE
Create the execution context with the configured runtimeConfig object.
C++ 1 IExecutionContext * context = engine -> createExecutionContext ( runtimeConfig );
Python 1 context = engine . create_execution_context ( runtime_config )
Run inference as usual on a non-default CUDA stream. TensorRT-RTX manages capture and replay automatically.
Sample Implementations
Example: tensorrt_rtx
RTX CUDA Graph support is available in tensorrt_rtx via the --rtxCudaGraphStrategy flag. This flag controls how CUDA Graphs are enabled and used during inference.
The following usage methods are available:
disable – Disables RTX CUDA Graph functionality.
wholeGraph (default) – Enables and captures the entire inference graph using CUDA Graphs.
Windows tensorrt_rtx . exe - -onnx = sample . onnx - -rtxCudaGraphStrategy = wholeGraph
Linux tensorrt_rtx --onnx = sample.onnx --rtxCudaGraphStrategy = wholeGraph
Note
While CUDA Graphs is now enabled by default when using the tensorrt_rtx executable, it is still disabled by default when using Python or C++ APIs.
Example: Open Source Software
Refer to the OSS sample implementations of the RTX CUDA Graph in the OSS repository:
[TensorRT-RTX OSS C++ API usage sample](<https://github.com/NVIDIA/TensorRT-RTX/blob/main/samples/apiUsage/cpp/apiUsage.cpp>)
[TensorRT-RTX OSS Python API usage sample](<https://github.com/NVIDIA/TensorRT-RTX/blob/main/samples/apiUsage/python/api_usage.py>)
Limitations
Tip
For applications involving dynamic shape inference, we strongly recommend using RTX CUDA Graphs via the native API, rather than implementing custom graph capture logic.
Even when enabled, the RTX CUDA Graph feature will not be utilized in the following scenarios:
Limitation
Why
Workaround
First inference iteration
RTX CUDA Graphs skips capture on the first iteration to allow setup and global driver calls; valid capture begins on the second iteration and replay on the third
Expect higher first-run latency; enable [runtime caching](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) to amortize JIT cost across application restarts
Default stream (stream 0) does not allow graph capture
The default CUDA stream (stream 0) is not capture-compatible
Use a dedicated non-default stream that supports capture
Stream already captured elsewhere
Nested or concurrent capture on the same stream is not permitted
Isolate TensorRT-RTX enqueue on its own stream; avoid overlapping capture sessions
Blocking GPU memory allocation strategy
Blocking allocation patterns interfere with graph capture
Use a non-blocking execution-context allocation strategy (for example, kSTATIC )
Data-dependent dynamic shapes or on-device control flow
Graph capture cannot represent runtime-dependent branches (for example, if-else nodes)
Simplify the network, disable CUDA graphs for that engine, or use fixed shapes where possible
Weight streaming during execution
Weights are not fully resident when kernels are captured
Load weights before capture or disable weight streaming for graph-enabled runs
Checking CUDA Graph Usage
When CUDA graph capture fails, TensorRT-RTX automatically falls back to the default execution path (non-graph execution). To detect whether graph capture succeeded, you can:
Use CUDA profiling tools (Nsight Systems) to verify graph execution
Check verbose application logs for any warnings related to graph capture
Monitor inference latency: Graph-captured execution typically has lower and more consistent latency
If graph capture fails repeatedly, review the limitations listed in the preceding section and consider adjusting your network architecture or execution context configuration.
Capture Stream Selection
Stream capture is [not allowed in the default stream](<https://docs.nvidia.com/cuda/cuda-programming-guide/04-special-topics/cuda-graphs.html#stream-capture>) (also known as the “NULL stream” or stream 0). You must assign a valid stream to enqueueV3 when using the CUDA graph feature. Refer to the [IExecutionContext::enqueueV3 reference](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/_static/c-api/classnvinfer1_1_1_i_execution_context.html#aa174ba57c44df821625ce4d3317dd7aa>) in the C++ API documentation.
