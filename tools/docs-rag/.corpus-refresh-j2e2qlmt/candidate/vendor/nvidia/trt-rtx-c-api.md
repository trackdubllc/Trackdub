Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/c-api-docs.html

C++ API Documentation
The [TensorRT-RTX C++ API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/_static/c-api/index.html>) for the NVIDIA TensorRT-RTX library lets developers import, generate, and deploy networks using C++. Networks can be imported directly from ONNX, or created programmatically by instantiating individual layers and setting parameters and weights directly.
This section walks through the basic C++ API workflow, assuming you start with an ONNX model. Refer to the samples in the [TensorRT-RTX open source repository](<https://github.com/NVIDIA/TensorRT-RTX/tree/main/samples>) for complete runnable programs.
The C++ API is accessed through the header NvInfer.h in the nvinfer1 namespace:
#include "NvInfer.h" using namespace nvinfer1 ;
Interface classes in the TensorRT-RTX C++ API begin with the prefix I , such as ILogger and IBuilder .
Note
A CUDA context is automatically created the first time TensorRT-RTX calls CUDA if none exists before that point. The auto-created context uses default settings and is associated with device 0.
Tip
Creating and configuring the CUDA context yourself is generally preferable before the first call to TensorRT-RTX, as it allows you to:
Specify which GPU device to use (for multi-GPU systems)
Set CUDA context flags (for example, cudaDeviceScheduleBlockingSync )
Configure memory allocation policies
Ensure proper error handling during context creation
To create a CUDA context manually, use cudaSetDevice() and cudaDeviceSynchronize() before calling any TensorRT-RTX API functions. For multi-GPU thread-safety guidance, refer to [Thread Safety](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#thread-safety>) .
The code in this chapter does not use smart pointers to illustrate object lifetimes; however, their use is recommended with TensorRT-RTX interfaces.
The Build Phase
The build phase converts an ONNX model (or a programmatic network definition) into a serialized TensorRT-RTX engine through ahead-of-time (AOT) compilation.
Create a logger by implementing ILogger . This example captures warning messages and ignores informational messages:
class Logger : public ILogger { void log ( Severity severity , const char * msg ) noexcept override { // suppress info-level messages if ( severity <= Severity :: kWARNING ) std :: cout << msg << std :: endl ; } } logger ;
Create the builder:
IBuilder * builder = createInferBuilder ( logger );
Creating a Network Definition
After the builder has been created, define the network that TensorRT-RTX will optimize.
Specify network creation flags by OR-ing NetworkDefinitionCreationFlag values together as needed for your application. Use 0 for the default network definition:
uint32_t flag = 0 ; // Optionally OR in flags, for example: // flag |= 1U << static_cast<uint32_t>(NetworkDefinitionCreationFlag::kSTRONGLY_TYPED);
Create the network definition:
INetworkDefinition * network = builder -> createNetworkV2 ( flag );
Creating a Network Definition from Scratch (Advanced) Instead of using a parser, you can define the network directly through the Network Definition API. This scenario assumes that per-layer weights are ready in host memory when you build the network.
This example creates a simple network with Input, Convolution, Pooling, MatrixMultiply, Shuffle, Activation, and SoftMax layers. Weights are loaded into a weightMap data structure that the following steps reference.
Attention
The snippets that follow are illustrative fragments , not a complete runnable program. They demonstrate the Network Definition API call shape and layer-stacking pattern, but elide weight loading ( weightMap / mWeightMap ), output sizes ( nbOutputs ), and sample helper types. For a complete, compilable end-to-end example of the same MNIST layer graph, refer to [Python API Documentation](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/python-api-docs.html#python-api-docs>) and the [network_api_pytorch_mnist](<https://github.com/NVIDIA/TensorRT/tree/main/samples/python/network_api_pytorch_mnist>) sample in the TensorRT OSS repository. For runnable C++ programs, including the recommended ONNX import path, refer to the [TensorRT-RTX OSS samples](<https://github.com/NVIDIA/TensorRT-RTX/tree/main/samples>) .
Create the builder and network definition the same way as in The Build Phase and Creating a Network Definition above:
IBuilder * builder = createInferBuilder ( logger ); INetworkDefinition * network = builder -> createNetworkV2 ( flag );
Add the Input layer to the network by specifying the input tensor’s name, datatype, and full dimensions. A network can have multiple inputs, although in this sample, there is only one:
auto data = network -> addInput ( INPUT_BLOB_NAME , datatype , Dims4 { 1 , 1 , INPUT_H , INPUT_W });
Add the Convolution layer with hidden layer input nodes, strides, and weights for filter and bias:
auto conv1 = network -> addConvolution ( * data , 20 , DimsHW { 5 , 5 }, weightMap [ "conv1filter" ], weightMap [ "conv1bias" ]); conv1 -> setStride ( DimsHW { 1 , 1 });
Note
Weights passed to TensorRT-RTX layers are in host memory.
Add the Pooling layer; the output from the previous layer is passed as input:
auto pool1 = network -> addPooling ( * conv1 -> getOutput ( 0 ), PoolingType :: kMAX , DimsHW { 2 , 2 }); pool1 -> setStride ( DimsHW { 2 , 2 });
Add the next Convolution and Pooling pair:
auto conv2 = network -> addConvolution ( * pool1 -> getOutput ( 0 ), 50 , DimsHW { 5 , 5 }, weightMap [ "conv2filter" ], weightMap [ "conv2bias" ]); conv2 -> setStride ( DimsHW { 1 , 1 }); auto pool2 = network -> addPooling ( * conv2 -> getOutput ( 0 ), PoolingType :: kMAX , DimsHW { 2 , 2 }); pool2 -> setStride ( DimsHW { 2 , 2 });
Add a Shuffle layer to reshape the tensor in preparation for matrix multiplication:
ITensor * pool2Output = pool2 -> getOutput ( 0 ); int32_t const batch = pool2Output -> getDimensions (). d [ 0 ]; int32_t const mmInputs = pool2Output -> getDimensions (). d [ 1 ] * pool2Output -> getDimensions (). d [ 2 ] * pool2Output -> getDimensions (). d [ 3 ]; auto inputReshape = network -> addShuffle ( * pool2Output ); inputReshape -> setReshapeDimensions ( Dims { 2 , { batch , mmInputs }});
Add a MatrixMultiply layer. The model exporter provided transposed weights, so specify kTRANSPOSE :
IConstantLayer * filterConst = network -> addConstant ( Dims { 2 , { nbOutputs , mmInputs }}, mWeightMap [ "ip1filter" ]); auto mm = network -> addMatrixMultiply ( * inputReshape -> getOutput ( 0 ), MatrixOperation :: kNONE , * filterConst -> getOutput ( 0 ), MatrixOperation :: kTRANSPOSE );
Add the bias, which will broadcast across the batch dimension:
auto biasConst = network -> addConstant ( Dims { 2 , { 1 , nbOutputs }}, mWeightMap [ "ip1bias" ]); auto biasAdd = network -> addElementWise ( * mm -> getOutput ( 0 ), * biasConst -> getOutput ( 0 ), ElementWiseOperation :: kSUM );
Add the ReLU Activation layer:
auto relu1 = network -> addActivation ( * biasAdd -> getOutput ( 0 ), ActivationType :: kRELU );
Add the SoftMax layer to calculate the final probabilities:
auto prob = network -> addSoftMax ( * relu1 -> getOutput ( 0 ));
Add a name for the output of the SoftMax layer so that the tensor can be bound to a memory buffer at inference time:
prob -> getOutput ( 0 ) -> setName ( OUTPUT_BLOB_NAME );
Mark it as the output of the entire network:
network -> markOutput ( * prob -> getOutput ( 0 ));
The network representing the MNIST model has now been fully constructed. For instructions on how to build a TensorRT-RTX engine and run inference with this network, refer to the Building a TensorRT-RTX Engine and Performing Inference sections.
For more information regarding layers, refer to the [TensorRT-RTX Operator documentation](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/_static/operators/index.html>) .
Importing a Model Using the ONNX Parser
Populate the network definition from an ONNX file using the ONNX parser.
Include the ONNX parser header. Note that ONNX parser APIs are defined in the nvonnxparser namespace:
#include "NvOnnxParser.h" using namespace nvonnxparser ;
Create an ONNX parser bound to the network and logger:
IParser * parser = createParser ( * network , logger );
Parse the ONNX file and print any parser errors:
parser -> parseFromFile ( modelFile , static_cast < int32_t > ( ILogger :: Severity :: kWARNING )); for ( int32_t i = 0 ; i < parser -> getNbErrors (); ++ i ) { std :: cout << parser -> getError ( i ) -> desc () << std :: endl ; }
Warning
The parser owns the memory occupied by the model weights. Do not delete the parser object until after the builder has run, or the network definition will contain dangling pointers leading to undefined behavior.
An important aspect of a TensorRT-RTX network definition is that it contains pointers to model weights, which the builder copies into the optimized engine. Since the network was created using the parser, the parser owns the memory occupied by the weights.
Building a TensorRT-RTX Engine
Configure the builder and serialize the optimized network to an engine buffer.
Create a build configuration:
IBuilderConfig * config = builder -> createBuilderConfig ();
This interface has many properties that control how TensorRT-RTX optimizes the network. One important property is the maximum workspace size. Layer implementations often require a temporary workspace, and this parameter limits the maximum size that any layer in the network can use. If insufficient workspace is provided, TensorRT-RTX may not be able to find an implementation for a layer.
Important
The default workspace limit is the total global memory of the device . Set an explicit limit whenever the builder shares the GPU with other work, such as building several engines on one device, building while an application is already running inference, or building on a display GPU.
Optionally, set the workspace memory pool limit:
config -> setMemoryPoolLimit ( MemoryPoolType :: kWORKSPACE , 1U << 20 );
Optionally, set the tactic shared-memory pool limit when TensorRT-RTX must coexist with other GPU consumers (for example, DirectX):
config -> setMemoryPoolLimit ( MemoryPoolType :: kTACTIC_SHARED_MEMORY , 48 << 10 );
Build the serialized engine:
IHostMemory * serializedModel = builder -> buildSerializedNetwork ( * network , * config );
Release builder objects that are no longer needed. The serialized engine contains its own weight copies:
delete parser ; delete network ; delete config ; delete builder ;
Save the engine to disk, then release the host memory buffer:
std :: ofstream engineFile ( "sample.engine" , std :: ios :: binary ); engineFile . write ( static_cast < char *> ( serializedModel -> data ()), serializedModel -> size ()); engineFile . close (); delete serializedModel ;
These steps constitute ahead-of-time (AOT) compilation in TensorRT-RTX. AOT compilation typically takes longer than just-in-time (JIT) compilation: often around 15 seconds for many networks (architecture overview cites a typical 20–30 second band depending on model and hardware). Compile and save the engine before deployment, during application install, or on first launch, depending on your network size and startup requirements.
Deserializing a TensorRT-RTX Engine
When you have a previously serialized optimized model and want to perform inference, create a runtime instance first.
Create the runtime with the same logger interface used during the build phase:
IRuntime * runtime = createInferRuntime ( logger );
TensorRT-RTX provides two main methods to deserialize an engine, each with its use case and benefits.
In-memory Deserialization Use this approach for smaller models or when holding the full engine in memory is acceptable.
Read the serialized engine into a host buffer.
Deserialize the engine from the buffer:
std :: vector < char > modelData = readModelFromFile ( "model.engine" ); ICudaEngine * engine = runtime -> deserializeCudaEngine ( modelData . data (), modelData . size ());
IStreamReaderV2 Deserialization Use this approach for custom file handling, large models, GPUDirect, or weight streaming. Reading the entire engine into a single buffer is optional because IStreamReaderV2 can supply data in chunks.
Implement IStreamReaderV2 with support for your storage backend (host or device memory).
Construct a reader for the engine file and deserialize:
class MyStreamReaderV2 : public IStreamReaderV2 { // Custom implementation with support for device memory reading }; MyStreamReaderV2 readerV2 ( "model.engine" ); ICudaEngine * engine = runtime -> deserializeCudaEngine ( readerV2 );
The IStreamReaderV2 approach reads the engine in chunks instead of requiring the whole serialized engine in host memory at once, so peak host memory during deserialization scales with the chunk size rather than the engine size. The benefit grows with engine size; measure against your own engine to decide whether it is worth the extra implementation.
When choosing a deserialization method, consider your specific requirements:
For small models or simple use cases, in-memory deserialization is often sufficient.
For large models or when memory efficiency is crucial, consider using IStreamReaderV2 (C++) or trt.IStreamReaderV2 (Python).
If you need custom file handling or weight streaming capabilities, IStreamReaderV2 provides the necessary flexibility.
Checking Engine Compatibility
Engines you create might not always be compatible with all TensorRT-RTX runtimes or all NVIDIA GPUs, for example, if you constrain the compute capabilities of your engine using the [Compute Capability API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) or because the engine was [compiled with a different version of TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/release-notes.html#release-notes>) .
Use IRuntime::getEngineValidity() (C++) or Runtime.get_engine_validity() (Python) to check whether an engine is expected to work before you deserialize the full payload. This API inspects only the engine header, so it is faster and uses less memory than full deserialization.
For compatibility scenarios, diagnostics bitmasks, and complete C++ and Python examples, refer to [Compatibility Checks](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#compatibility-checks>) .
int64_t const nbHeaderBytes = runtime -> getEngineHeaderSize (); auto dataPtr = static_cast < void const *> ( serializedEngine -> data ()); uint64_t diagnostics ; auto const validity = runtime -> getEngineValidity ( dataPtr , nbHeaderBytes , & diagnostics ); if ( validity == nvinfer1 :: EngineValidity :: kINVALID ) { std :: cerr << "Engine can not be executed." << std :: endl ; }
Refer to the [sample code](<https://github.com/NVIDIA/TensorRT-RTX/blob/main/samples/apiUsage/cpp/apiUsage.cpp>) for a complete example.
Performing Inference
The engine holds the optimized model. Manage runtime inference with an execution context.
Create an execution context from the deserialized engine:
IExecutionContext * context = engine -> createExecutionContext ();
An engine can have multiple execution contexts, allowing one set of weights to be used for multiple overlapping inference tasks. Creating the execution context triggers JIT compilation of portions of your network’s engine. This step is fairly quick, but you can speed up subsequent runs by using IRuntimeCache . For more information, refer to the [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) section.
Allocate device buffers for the input and output tensors. Size each allocation from the engine’s tensor shape and data type:
void * inputBuffer = nullptr ; void * outputBuffer = nullptr ; cudaMalloc ( & inputBuffer , inputByteSize ); cudaMalloc ( & outputBuffer , outputByteSize );
Bind input and output tensors to GPU buffers using the tensor names from the network definition:
context -> setTensorAddress ( INPUT_NAME , inputBuffer ); context -> setTensorAddress ( OUTPUT_NAME , outputBuffer );
These buffers must remain allocated at the same device address until inference completes. Refer to [Buffer Lifetime](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#buffer-lifetime>) .
If the engine was built with dynamic shapes, set the runtime input dimensions before enqueue:
context -> setInputShape ( INPUT_NAME , inputDims );
Enqueue inference on a CUDA stream:
context -> enqueueV3 ( stream );
Synchronize the stream (or wait on a CUDA event) to determine when the inference pass is complete.
Free the device buffers after the stream synchronizes:
cudaFree ( inputBuffer ); cudaFree ( outputBuffer );
