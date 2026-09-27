Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html

Engine Compatibility
This page covers TensorRT-RTX engine compatibility across releases and target hardware, including release compatibility requirements and lightweight validity checks before deserialization.
Version Compatibility
Warning
TensorRT-RTX engines are not compatible between different releases of TensorRT-RTX. If you deploy a new version of the TensorRT-RTX runtime library, you must also provide a new engine compiled with the same version. Refer to Compatibility Checks for instructions on determining engine compatibility using the [IRuntime::getEngineValidity()](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/_static/c-api/classnvinfer1_1_1_i_runtime.html#ac065f4ac6a37a1a60be100f5129eba3b>) API.
If you choose on-device AOT compilation, ensure that your application rebuilds the engine when you update the TensorRT-RTX runtime library or when the end user upgrades to a newer NVIDIA GPU. Refer to [Porting Guide for TensorRT Applications](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>) for deployment strategies.
Compatibility Checks
Serialized TensorRT-RTX engine files can be incompatible with the current installation of TensorRT-RTX for the reasons below.
Table 3 Common engine invalidity causes Cause
Typical diagnostic flag / symptom
Remedy
TensorRT-RTX version mismatch
Version mismatch in validity check
Rebuild the engine for the installed TensorRT-RTX version
Compute capability mismatch
Target compute capability not supported on device
Rebuild with appropriate IBuilderConfig compute capabilities
CUDA driver or runtime too old
kOLD_CUDA_DRIVER / kOLD_CUDA_RUNTIME
Update CUDA on the system, or rebuild the engine for the older stack
Insufficient GPU memory for weights
kINSUFFICIENT_GPU_MEMORY
Rebuild with [Weight Streaming](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/weight-streaming.html#weight-streaming>) enabled
Malformed or wrong product engine file
Generic invalid result
Rebuild with TensorRT-RTX; verify file integrity (for example SHA-256)
In all these situations, the serialized engine needs to be rebuilt either:
programmatically using IBuilder::buildSerializedNetwork() (refer to the buildEngine() function in [Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>) for an example), or
on the command line with tensorrt_rtx --saveEngine (refer to the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) for the command flow and [Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) for deployment timing),
to be usable for inference on the current system.
To understand whether a rebuild is necessary, one option is trying to deserialize the engine with IRuntime::deserializeCudaEngine() and noting whether the call fails by returning a null pointer. However, this requires loading the entire serialized engine into a memory buffer, which can be resource intensive or slow. Therefore we recommend using getEngineValidity() prior to deserializeCudaEngine() .
Use the decision tree below to choose between a lightweight header check and a full rebuild, then call IRuntime::getEngineValidity() to inspect only the engine header.
Important
getEngineValidity() only inspects a short header (64 bytes at time of writing, but use getEngineHeaderSize() ): it does not fully validate the engine. A result of EngineValidity::kVALID means deserialization is likely to succeed, not guaranteed.
Instead, call the IRuntime::getEngineValidity() API to only inspect the engine header for metadata indicating whether deserialization is likely to succeed or fail. This only requires loading a small number of bytes from the start of the engine file into main memory, which can be queried with IRuntime::getEngineHeaderSize() . If the header indicates that the engine is not compatible with the current system, IRuntime::getEngineValidity() will return EngineValidity::kINVALID and the reasons for the failure will be flagged via a bitmask output argument. The typical use is as follows:
C++ 1 auto nbBytes = runtime -> getEngineHeaderSize (); 2 const char * engineFilePath = "/path/to/engine" ; 3 std :: vector < char > headerData ( nbBytes ); 4 std :: ifstream file ( engineFilePath , std :: ios_base :: binary ); 5 if ( ! file . read ( & headerData [ 0 ], nbBytes )){ 6 throw "File too short" ; 7 } 8 uint64_t diagnostics ; 9 auto validity = runtime -> getEngineValidity ( & headerData [ 0 ], nbBytes , & diagnostics ); 10 if ( validity == EngineValidity :: kINVALID ){ 11 bool needsWeightsStreaming = diagnostics & uint64_t ( EngineInvalidityDiagnostics :: kINSUFFICIENT_GPU_MEMORY ); 12 bool incorrectCuda = diagnostics & uint64_t ( EngineInvalidityDiagnostics :: kCUDA_ERROR ); 13 if ( incorrectCuda ){ 14 // Signal to user to fix their CUDA installation 15 } else if ( needsWeightsStreaming ){ 16 // rebuild the engine with weight streaming enabled 17 } else { 18 // rebuild the engine 19 } 20 }
Python 1 import tensorrt_rtx as trt 2 3 nb_bytes = runtime . engine_header_size 4 file_path = "/path/to/engine" 5 with open ( file_path , "rb" ) as f : 6 file_bytes = f . read ( nb_bytes ) 7 buffer = memoryview ( file_bytes ) 8 valid , diagnostics = runtime . get_engine_validity ( buffer ) 9 if valid == trt . EngineValidity . INVALID : 10 needs_weight_streaming = bool ( diagnostics & trt . EngineValidityDiagnostics . INSUFFICIENT_GPU_MEMORY . value ) 11 incorrect_cuda = bool ( diagnostics & trt . EngineValidityDiagnostics . CUDA_ERROR . value ) 12 if incorrect_cuda : 13 # Signal to user to fix their CUDA installation 14 elif needs_weight_streaming : 15 # Rebuild engine with weight streaming enabled 16 else : 17 # Rebuild the engine
Engine invalidity diagnostics reference Multiple error conditions can occur (CUDA runtime too old, CUDA driver too old, mismatching TensorRT-RTX version, and more), but most share the same remedy: rebuild the TensorRT-RTX AOT engine for the current system. The granular information helps application developers debug.
Table 4 Common EngineInvalidityDiagnostics flags Diagnostic flag
Typical remedy
kCUDA_ERROR / CUDA_ERROR
Fix the CUDA installation (restart, reinstall driver/runtime); do not rebuild the engine first.
kOLD_CUDA_DRIVER / OLD_CUDA_DRIVER
Update the CUDA driver, or rebuild the engine for the installed driver version.
kOLD_CUDA_RUNTIME / OLD_CUDA_RUNTIME
Update the CUDA runtime bundled with your app, or rebuild the engine.
kINSUFFICIENT_GPU_MEMORY / INSUFFICIENT_CUDA_MEMORY
Rebuild with [Weight Streaming](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/weight-streaming.html#weight-streaming>) enabled.
Version / compute-capability mismatch
Rebuild the engine for the current TensorRT-RTX version and target GPU.
Treat diagnostics as a bit mask; multiple error conditions can be simultaneously true (corresponding to multiple bits set to 1 ).
Apart from EngineValidity::kVALID and EngineValidity::kINVALID , the IRuntime::getEngineValidity() API can also return EngineValidity::kSUBOPTIMAL to indicate that the engine can be used for inference but with suboptimal performance. This can happen when the installed GPU belongs to a newer architecture than was available when TensorRT-RTX was built (in this release, a compute capability greater than 12.1). Unless the engine has been built for a specific (older) compute capability, it can still support inference on these newer architectures via forward-compatible PTX assembly but the performance is likely not going to be on par with native Cubin code. Achieving state-of-the-art performance will require updating TensorRT-RTX in addition to rebuilding the engines.
Finally, keep in mind that IRuntime::getEngineValidity() only inspects a short header and therefore cannot guarantee that engine deserialization will succeed even if the return value is EngineValidity::kVALID . (However, returning EngineValidity::kINVALID guarantees that the deserialization will fail.) The reason is that later segments in the serialized engine file may have become corrupted. However, if users can verify that the engine file has been produced by TensorRT-RTX without corruption (for example, by checking a SHA-256 hash), they can expect that a successful validity check will result in successful deserialization.
For weight-stripped engines that need runtime refit, refer to [Refitting Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/refitting-engines.html#refitting-engines>) and [CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) . For runtime-cache persistence (JIT kernel reuse), refer to [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) .
