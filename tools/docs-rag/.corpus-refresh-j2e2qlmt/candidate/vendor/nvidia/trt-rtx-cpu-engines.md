Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html

CPU-Only AOT and TensorRT-RTX Engines
The CPU-only ahead-of-time (AOT) feature is enabled by default. When you build an engine, TensorRT-RTX does not require a GPU device to be present. The engine is portable to both Windows and Linux operating systems with RTX GPUs that have compute capability 8.6 or later (Ampere and later).
All model weights are stored inside the engine by default. NVIDIA Turing devices are not supported without further configuration. This section provides details about the Compute Capability API and instructions for building a weightless engine for deployment with minimal storage footprint.
Table 11 CPU AOT engine types and when to use them Engine type
GPU at AOT build
Weights in serialized file
When to use
Default CPU-only AOT
Not required
Full weights embedded
Standard RTX deployment; portable engine for Ampere and later (8.6+) by default
Explicit compute capability
Not required (unless kCURRENT )
Full weights embedded
Target Turing (7.5) or lock to specific CC list; see [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) for precision limits
Weightless + refit ( kSTRIP_PLAN + kREFIT )
Required; at most one CC
Stripped; refit at first launch
Minimal installer footprint; distribute small engine and restore weights on the client
Compute Capability
[Compute capability](<https://docs.nvidia.com/cuda/cuda-c-programming-guide/index.html#compute-capabilities>) defines the hardware features and supported instructions for each NVIDIA GPU architecture. For information regarding the compute capability of your GPU, refer to [NVIDIA CUDA GPU Compute Capability](<https://developer.nvidia.com/cuda-gpus>) .
By default, an engine built by TensorRT-RTX can be run on GPU devices with compute capability 8.6, 8.9, 12.0, 12.1, and later. To build engines for Turing devices, a compute capability of 7.5 needs to be specified through the API.
Specifying Target Compute Capabilities
You can specify one or more compute capabilities through IBuilderConfig . The following example shows how to set a single target compute capability of 7.5 for Turing RTX devices.
C++ 1 IBuilderConfig * config = builder -> createBuilderConfig (); 2 config -> setNbComputeCapabilities ( 1 ); 3 config -> setComputeCapability ( ComputeCapability :: kSM75 , 0 );
Python 1 import tensorrt_rtx as trt 2 3 builder_config = builder . create_builder_config () 4 builder_config . num_compute_capabilities = 1 5 builder_config . set_compute_capability ( trt . ComputeCapability . SM75 , 0 )
Table 12 Compute capability configuration options Configuration
AOT build behavior
When to use
Default (no CC set)
CPU-only AOT; engine runs on CC 8.6, 8.9, 12.0, 12.1, and later
Most RTX apps without Turing-only requirements
Explicit CC list ( setComputeCapability / --computeCapabilities )
Engine limited to listed architectures
Turing (7.5) support or multi-arch targeting with known trade-offs
`ComputeCapability::kCURRENT` / --useGpu
Requires GPU; compiles for the device in the build environment
Development tuning on a specific GPU; must be the only CC target
You can use the ComputeCapability::kCURRENT flag to turn off CPU-only AOT and use the current GPU device in the environment as the target to compile an engine. ComputeCapability::kCURRENT is supported only when it is the sole target compute capability.
When you set one or multiple compute capabilities, the engine you build can only run on specified target devices.
Warning
If you specify Turing (compute capability 7.5) as one of multiple targets, the resulting engine is not guaranteed to be performant on Ampere or later devices. Build separate engines per architecture for best performance.
The tensorrt_rtx tool provides the flag --useGpu to be equivalent to setting ComputeCapability::kCURRENT ; and option --computeCapabilities=<list_of_CCs> to accept one or multiple compute capabilities.
Compile Models with Hardware-Specific Data Types
When hardware-specific data types appear in the network definition, TensorRT-RTX will either emit a warning message or an error based on how the API is called by users. For per-architecture precision support, refer to the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) . If you are porting a model that uses reduced-precision types, also refer to [Work With Quantized Types](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-quantized-types.html#work-with-quantized-types>) .
Commonly used hardware-specific data types include FP4, FP8, BF16, and INT4. FP16, FP32, and other integer types are supported on all supported hardware.
The behavior of TensorRT-RTX depends on whether one or more compute capabilities are specified:
Table 13 Hardware-specific data type behavior by compute capability configuration CC configuration
Default precision behavior
Failure mode
Default (none specified)
FP8 models: Ada and later (8.9+). FP4 models: Blackwell and later (12.0+). All other models: Ampere and later (8.6+).
Runtime failure on hardware below the implied minimum
One or more CCs specified
Engine is limited to the specified compute capabilities
Build error recorded by IErrorRecorder if the model uses a data type unsupported by any specified CC
Weightless Engines
An engine without weights helps minimize the storage footprint for deployment. Weight-stripping build configuration can be enabled, so that TensorRT-RTX enables refit only for constant weights that do not impact the builder’s ability to optimize and produce an engine with the same runtime performance as a non-refittable engine. Those weights are then omitted from the serialized engine, resulting in a small engine file that can be refitted at runtime using custom weights or weights from the ONNX model.
Note
When weight-stripping build configuration is enabled, a GPU device is required for the AOT build and at most one compute capability can be set through the builder config.
Building a Weightless Engine
The tensorrt_rtx tool provides flags --stripWeights and --refit to enable the weight-stripping build configuration. The corresponding builder flags are kSTRIP_PLAN and kREFIT .
C++ 1 ... 2 config -> setFlag ( BuilderFlag :: kSTRIP_PLAN ); 3 config -> setFlag ( BuilderFlag :: kREFIT ); 4 builder -> buildSerializedNetwork ( * network , * config );
Python 1 ... 2 config . flags |= 1 << int ( trt . BuilderFlag . STRIP_PLAN ) 3 config . flags |= 1 << int ( trt . BuilderFlag . REFIT ) 4 builder . build_serialized_network ( network , config )
After the engine is built, save the engine file and distribute it to the installer.
Refitting a Weightless Engine
On the client side, when launching the network for the first time, the user can refit all the weights back to the engine. Since all the weights in the engine were removed, each weight needs to be updated one by one. After all weights are updated, save the full TensorRT-RTX engine file to be used in the application for future inference.
C++ 1 IRefitter * refitter = createInferRefitter ( * engine , gLogger ); 2 int32_t const n = refitter -> getAllWeights ( 0 , nullptr ); 3 for ( int32_t i = 0 ; i < n ; ++ i ) { 4 refitter -> setNamedWeights ( weightsNames [ i ], Weights {...}); 5 } 6 auto serializationConfig = SampleUniquePtr < nvinfer1 :: ISerializationConfig > ( cudaEngine -> createSerializationConfig ()); 7 auto serializationFlag = serializationConfig -> getFlags () 8 serializationFlag &= ~ ( 1 << static_cast < uint32_t > ( nvinfer1 :: SerializationFlag :: kEXCLUDE_WEIGHTS )); 9 serializationConfig -> setFlags ( serializationFlag ); 10 // hostMemory will contain the full engine 11 auto hostMemory = SampleUniquePtr < nvinfer1 :: IHostMemory > ( cudaEngine -> serializeWithConfig ( * serializationConfig ));
Python 1 refitter = trt . Refitter ( engine , TRT_LOGGER ) 2 all_weights = refitter . get_all () 3 for name in all_weights : 4 refitter . set_named_weights ( name , weights [ name ]) 5 serialization_config = engine . create_serialization_config () 6 serialization_config . flags &= ~ ( 1 << int ( trt . SerializationFlag . EXCLUDE_WEIGHTS )) 7 binary = engine . serialize_with_config ( serialization_config )
Refitting a Weightless Engine Directly with ONNX Models When working with weight-stripped engines created from ONNX models, the refit process can be done automatically with the IParserRefitter class from the ONNX parser library. The following code shows how to create the class and run the refit process.
C++ 1 IRefitter * refitter = createInferRefitter ( * engine , gLogger ); 2 IParserRefitter * parserRefitter = createParserRefitter ( * refitter , gLogger ); 3 bool result = parserRefitter -> refitFromFile ( "path_to_onnx_model" ); 4 refitSuccess = refitter -> refitCudaEngine ();
Python 1 refitter = trt . Refitter ( engine , TRT_LOGGER ) 2 parser_refitter = trt . OnnxParserRefitter ( refitter , TRT_LOGGER ) 3 result = parser_refitter . refit_from_file ( "path_to_onnx_model" ) 4 refit_success = refitter . refit_cuda_engine ()
Deferred Weights Loading
Use deferred weights loading to deserialize a TensorRT-RTX engine without
allocating the engine weights in host or GPU memory. During execution context
creation, TensorRT-RTX just-in-time (JIT) compiles the required kernels from the
metadata in the weightless engine. This behavior lowers peak host and GPU memory
use during deserialization. It is useful when an application deserializes
several engines before running any of them, such as a multi-model application
that keeps many engines resident.
Warning
Deferred weights loading and [weight streaming](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/weight-streaming.html#weight-streaming>) are
mutually exclusive in the current release. Enabling both features results
in an API usage error.
Because JIT compilation runs during createExecutionContext() , you can
optionally attach an IRuntimeCache to the runtime configuration and
serialize the compiled kernels to disk. This approach lets multiple models
populate runtime caches in parallel without loading weights into host or GPU
memory. The optional cache steps are marked in the following example. Keep the IRuntimeConfig and IRuntimeCache alive while contexts created from them
are in use. For more information, refer to [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) .
C++ 1 IRuntime * runtime = createInferRuntime ( gLogger ); 2 runtime -> setDeferredWeightsLoading ( true ); 3 ICudaEngine * engine = runtime -> deserializeCudaEngine ( blob , size ); 4 5 // [Optional] Attach a runtime cache to save the JIT-compiled 6 // kernels without loading weights. The config and cache must 7 // outlive contexts created from them. 8 IRuntimeConfig * config = engine -> createRuntimeConfig (); 9 IRuntimeCache * cache = config -> createRuntimeCache (); 10 config -> setRuntimeCache ( * cache ); 11 12 IExecutionContext * context = engine -> createExecutionContext ( config ); 13 14 // [Optional] Serialize the runtime cache into host memory for 15 // saving to disk. 16 IHostMemory * hostMemory = cache -> serialize (); 17 18 // Before the first inference, load the weights into GPU memory 19 // from the same plan blob. 20 engine -> loadWeights ( blob , size ); 21 context -> enqueueV3 ( stream );
Python 1 runtime = trt . Runtime ( TRT_LOGGER ) 2 runtime . defer_weights_loading = True 3 engine = runtime . deserialize_cuda_engine ( blob ) 4 5 # [Optional] Attach a runtime cache to save the JIT-compiled 6 # kernels without loading weights. Keep config and cache alive 7 # while contexts created from them are in use. 8 config = engine . create_runtime_config () 9 cache = config . create_runtime_cache () 10 config . set_runtime_cache ( cache ) 11 12 context = engine . create_execution_context ( config ) 13 14 # [Optional] Save the compiled kernels to disk. 15 with cache . serialize () as serialized_cache : 16 with open ( "runtime.cache" , "wb" ) as cache_file : 17 cache_file . write ( serialized_cache ) 18 19 # Before the first inference, load the weights into GPU memory 20 # from the same plan blob. 21 engine . load_weights ( blob ) 22 assert engine . weights_loaded 23 context . execute_async_v3 ( stream )
Loading Weights Directly to the GPU To transfer weights directly from storage to the GPU without staging a
complete copy in host memory, use loadWeightsAsync() with an IStreamReaderV2 that serves the engine plan bytes on demand. loadWeightsAsync() does not synchronize the stream. Enqueue dependent
work on the same stream, and synchronize the stream explicitly when needed.
C++ 1 // myReader implements IStreamReaderV2 and serves the engine plan 2 // bytes on demand. 3 engine -> loadWeightsAsync ( myReader , stream ); 4 context -> enqueueV3 ( stream ); 5 // ... 6 cudaStreamSynchronize ( stream );
Python 1 engine . load_weights_async ( my_reader , int ( stream )) 2 context . execute_async_v3 ( int ( stream )) 3 # ... 4 cudart . cudaStreamSynchronize ( stream )
Next Steps
[Porting from TensorRT](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>) : Deploying CPU-only ahead-of-time engines in your application
[Simultaneous Compute and Graphics](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/compute-graphics.html#compute-graphics>) : Running inference alongside graphics workloads
[Best Practices](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/best-practices.html#best-practices>) : Measuring and optimizing inference performance
