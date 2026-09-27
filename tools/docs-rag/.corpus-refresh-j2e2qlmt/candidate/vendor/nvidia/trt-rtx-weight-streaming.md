Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/weight-streaming.html

Weight Streaming
Weight streaming enables inference for models whose weights are too large to fit entirely in device memory. TensorRT-RTX keeps part of the weights in host memory and transfers them to the device as needed.
Table 7 Weight streaming configuration summary Stage
API / flag
What to set
Build (AOT)
BuilderFlag::kWEIGHT_STREAMING / tensorrt_rtx --allowWeightStreaming
Serialize a streamable engine with buildSerializedNetwork()
Runtime (inference)
ICudaEngine::setWeightStreamingBudgetV2() / weight_streaming_budget_v2 / tensorrt_rtx --weightStreamingBudget
Bytes of streamable weights to keep on device; trade memory for performance
Configure Weight Streaming with the API
Build a Streamable Engine
To enable weight streaming at build time, set kWEIGHT_STREAMING in the builder configuration:
C++ 1 auto config = builder -> createBuilderConfig (); 2 config -> setFlag ( BuilderFlag :: kWEIGHT_STREAMING ); 3 auto hostMem = builder -> buildSerializedNetwork ( * network , * config );
Python 1 config = builder . create_builder_config () 2 config . set_flag ( tensorrt_rtx . BuilderFlag . WEIGHT_STREAMING ) 3 hostMem = builder . build_serialized_network ( network , config )
Set the Runtime Memory Budget
After deserializing the engine, set the number of bytes of streamable weights to keep in device memory. A smaller budget reduces device-memory use but can reduce performance. Choose a budget based on the available device memory and the engine’s streamable weight size:
C++ 1 auto nbBytesWeights = engine -> getStreamableWeightsSize (); 2 size_t nbBytesDeviceFree ; 3 size_t nbBytesDeviceTotal ; 4 cudaMemGetInfo ( & nbBytesDeviceFree , & nbBytesDeviceTotal ); 5 // For this example, assume the weights to be kept in device memory 6 // should be the smaller of: 7 // (1) half the free device memory 8 // (2) half the total streamable weights 9 auto weightBudget = std :: min ( 10 static_cast < int64_t > ( nbBytesDeviceFree / 2 ), 11 nbBytesWeights / 2 ); 12 engine -> setWeightStreamingBudgetV2 ( weightBudget );
Python 1 nbBytesWeights = engine . streamable_weights_size 2 free , total = pycuda . driver . mem_get_info () 3 # For this example, assume the weights to be kept in device memory 4 # should be the smaller of: 5 # (1) half the free device memory 6 # (2) half the total streamable weights 7 weightBudget = min ( free // 2 , nbBytesWeights // 2 ) 8 engine . weight_streaming_budget_v2 = weightBudget
Configure Weight Streaming with tensorrt_rtx
Use tensorrt_rtx --allowWeightStreaming to build a streamable engine. Set the runtime budget with --weightStreamingBudget . This option accepts:
-2 : Disable streaming at runtime. This is the default.
-1 : Select the budget automatically.
A percentage, such as 50% , of streamable weights to keep resident.
An explicit byte count, such as 500M .
For example, a budget of 50% for a 50 MB streamable weight set keeps 25 MB resident and streams the remainder.
Note
Weight streaming is incompatible with CUDA graph capture. TensorRT-RTX’s built-in CUDA Graphs will be deactivated automatically if weight streaming is enabled.
