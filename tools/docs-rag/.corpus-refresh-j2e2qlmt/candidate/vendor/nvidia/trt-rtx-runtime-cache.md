Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html

Working with Runtime Cache
TensorRT-RTX by default compiles GPU kernels during runtime. Runtime caching helps reduce the startup overhead of GPU kernel compilation by storing compiled kernels on disk for reuse.
Overview
When you enable runtime cache, the system can serialize kernels compiled at runtime to a binary blob and save it to a disk file. You can reload this serialized runtime cache to avoid recompilation overhead during inference. This significantly improves performance in workflows with frequent kernel reuse or repeated application runs, resulting in faster inference startup times and a smoother user experience. Runtime caching is especially beneficial in production environments or iterative development workflows where minimizing latency is critical.
Compatibility Checks
When using a pre-populated runtime cache, the cache may have been created in a different or outdated environment. The runtime cache validates the TensorRT-RTX engine against the cached blob before reuse:
Check
Requirement
Failure behavior
GPU hardware
The engine’s GPU must be equivalent to the cached version
Cache is ignored; kernels are JIT-compiled again (no error raised)
TensorRT-RTX version
Engine TensorRT-RTX version must be equal to the cached version
Cache is ignored; kernels are JIT-compiled again (no error raised)
NVIDIA driver version
Engine driver version must be greater than or equal to the cached version
Cache is ignored; kernels are JIT-compiled again (no error raised)
CUDA-Context CiG (Compute in Graphics mode)
Engine CUDA-context CiG state must be equal to the cached version
Cache is ignored; kernels are JIT-compiled again (no error raised)
When any check fails, the system falls back to JIT compilation automatically and transparently to the application. The first inference run after a failed match can take longer while kernels are recompiled. To verify whether a cache was used, check the application logs or use profiling tools to measure JIT compilation time.
APIs
Runtime caching is a feature introduced to TensorRT-RTX’s execution context creation. Ensure the application has completed the necessary steps up to deserializing the TensorRT-RTX engine. The deserialized engine should be available to create an execution context.
Creating the Runtime Cache
During context creation and inference, TRT-RTX will compile the necessary kernels for inference. Each of them will be added to the runtime cache without duplication.
Create a runtimeConfig object from the engine. Set the appropriate allocation strategy for the execution context.
C++ 1 IRuntimeConfig * runtimeConfig = engine -> createRuntimeConfig (); 2 3 runtimeConfig -> setExecutionContextAllocationStrategy ( ExecutionContextAllocationStrategy :: kSTATIC );
Python 1 runtime_config = engine . create_runtime_config () 2 3 runtime_config . set_execution_context_allocation_strategy ( trt . ExecutionContextAllocationStrategy . STATIC )
Create the runtimeCache object and set it to the runtimeConfig object.
C++ 1 IRuntimeCache * runtimeCache = runtimeConfig -> createRuntimeCache (); 2 3 runtimeConfig -> setRuntimeCache ( * runtimeCache );
Python 1 runtime_cache = runtime_config . create_runtime_cache () 2 3 runtime_config . set_runtime_cache ( runtime_cache )
Create the execution context with the configured runtimeConfig object.
C++ 1 IExecutionContext * context = engine -> createExecutionContext ( runtimeConfig );
Python 1 context = engine . create_execution_context ( runtime_config )
Load and Save the Runtime Cache
Your application may need to run inference on the same or similar models repeatedly. In such cases, saving the runtime cache to disk allows previously compiled GPU kernels to be loaded and reused across runs.
TensorRT-RTX provides sample utility functions to load the cache file on disk to memory for reuse. The utility functions loadCacheFile and saveCacheFile are good references to load and save memory blobs from and to storage. The loaded bytes can be used for deserialization before the runtime cache is placed into the runtimeConfig object. After inference, serialized runtime cache can be saved to file for reuse in consecutive runs.
Prerequisites
The Python examples use Polygraphy file utilities. Install Polygraphy before
running them:
python3 -m pip install polygraphy
Steps
Load the runtime cache and run inference.
C++ Listing 7 Load a runtime cache from disk ( samplesCommon::loadCacheFile sample helper)
1 std :: vector < char > loadedCacheBytes 2 = samplesCommon :: loadCacheFile ( sample :: gLogger , "runtime.cache" ); 3 4 if ( ! loadedCacheBytes . empty ()) 5 { 6 std :: vector < uint8_t > runtimeCacheBytes ( 7 loadedCacheBytes . begin (), loadedCacheBytes . end ()); 8 9 runtimeCache -> deserialize ( 10 runtimeCacheBytes . data (), runtimeCacheBytes . size ()); 11 12 runtimeConfig -> setRuntimeCache ( * runtimeCache ); 13 }
Python Listing 8 Load a runtime cache from disk (Polygraphy util.load_file ; analogous to the C++ sample helper)
1 # Use TensorRT's polygraphy library to load and deserialize cache files 2 from polygraphy import util 3 4 runtime_cache_file = "runtime.cache" 5 loaded_cache_bytes = None 6 try : 7 with util . LockFile ( runtime_cache_file ): 8 loaded_cache_bytes = util . load_file ( runtime_cache_file ) 9 except FileNotFoundError : 10 pass 11 12 if loaded_cache_bytes : 13 runtime_cache . deserialize ( loaded_cache_bytes ) 14 15 runtime_config . set_runtime_cache ( runtime_cache )
After inference is complete, serialize the runtime cache to save it to disk. Save the runtime cache in binary format (for example, std::ios::binary ) instead of a text file.
C++ Listing 9 Serialize and save the runtime cache ( samplesCommon::saveCacheFile sample helper)
1 // get the runtime config from the execution context 2 IRuntimeConfig * runtimeConfig = context -> getRuntimeConfig (); 3 4 // get the runtime cache from the runtime config 5 IRuntimeCache * runtimeCache = runtimeConfig -> getRuntimeCache (); 6 7 // serialize the cache into a memory blob 8 IHostMemory * hostMemory = runtimeCache -> serialize (); 9 assert ( hostMemory != nullptr ); 10 11 // save the serialized cache to disk 12 samplesCommon :: saveCacheFile ( 13 sample :: gLogger , "runtime.cache" , hostMemory );
Python Listing 10 Serialize and save the runtime cache (Polygraphy util.save_file ; analogous to the C++ sample helper)
1 # get the runtime config from the execution context 2 runtime_config = context . get_runtime_config () 3 4 # get the runtime cache from the runtime config 5 runtime_cache = runtime_config . get_runtime_cache () 6 7 # serialize the cache into a memory blob and save to disk 8 with util . LockFile ( runtime_cache_file ): 9 with runtime_cache . serialize () as buffer : 10 util . save_file ( buffer , runtime_cache_file , 11 description = "runtime cache" )
tensorrt_rtx Example
Runtime caching is added to tensorrt_rtx with the --runtimeCacheFile flag, which takes in a file path to the runtime cache file on disk. Ensure that the provided file path has the read and write permissions.
Windows tensorrt_rtx . exe - -onnx =.\ sample . onnx - -runtimeCacheFile =.\ runtime . cache
Linux tensorrt_rtx --onnx = sample.onnx --runtimeCacheFile = ./runtime.cache
The first tensorrt_rtx run will fill the cache with the compilation information and serialize it to the specified file. The following tensorrt_rtx runs can reuse the cache file to speed up inference. The acceleration is greatest when the runtime cache is used for the same or similarly-structured models.
