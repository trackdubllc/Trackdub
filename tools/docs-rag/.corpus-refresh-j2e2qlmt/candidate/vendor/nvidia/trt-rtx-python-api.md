Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/python-api-docs.html

Python API Documentation
The [TensorRT-RTX Python API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/_static/python-api/index.html>) enables developers in Python-based development environments: including those looking to experiment with TensorRT-RTX: to parse models (for example, from ONNX) and to generate and run TensorRT-RTX engine files.
This section walks through the basic Python API workflow, assuming you start with an ONNX model.
The Python API is accessed through the tensorrt_rtx module:
import tensorrt_rtx as trt
The Build Phase
The build phase converts an ONNX model (or a programmatic network definition) into a serialized TensorRT-RTX engine through ahead-of-time (AOT) compilation.
Create a logger. The Python bindings include a simple logger that prints messages at or above a chosen severity to stdout :
logger = trt . Logger ( trt . Logger . WARNING )
Alternatively, define your own logger by deriving from ILogger :
import logging class MyLogger ( trt . ILogger ): def __init__ ( self ): trt . ILogger . __init__ ( self ) self . _log = logging . getLogger ( "tensorrt_rtx" ) def log ( self , severity , msg ): if severity <= trt . Logger . ERROR : self . _log . error ( msg ) else : self . _log . info ( msg ) logger = MyLogger ()
Create the builder:
builder = trt . Builder ( logger )
Note
Building engines is intended as an offline process, so it can take significant time.
Creating a Network Definition
After the builder has been created, define the network that TensorRT-RTX will optimize.
Specify network creation flags by OR-ing NetworkDefinitionCreationFlag values together as needed for your application. Use 0 for the default network definition:
flag = 0 # Optionally OR in flags, for example: # flag |= 1 << int(trt.NetworkDefinitionCreationFlag.STRONGLY_TYPED)
Create the network definition:
network = builder . create_network ( flag )
Creating a Network Definition from Scratch (Advanced) Instead of using a parser, you can define the network directly through the Network Definition API. This scenario assumes that per-layer weights are ready in host memory when you build the network.
The code corresponding to this section can be found in [network_api_pytorch_mnist](<https://github.com/NVIDIA/TensorRT/tree/main/samples/python/network_api_pytorch_mnist>) .
This example creates a simple network with Input, Convolution, Pooling, MatrixMultiply, Shuffle, Activation, and SoftMax layers.
Attention
The snippets that follow are illustrative fragments aligned with the sample above, not a complete runnable program. They elide helper functions (such as add_matmul_as_fc ), output sizes ( nbOutputs ), and weight-loading setup. For a complete, compilable end-to-end example, run the [network_api_pytorch_mnist](<https://github.com/NVIDIA/TensorRT/tree/main/samples/python/network_api_pytorch_mnist>) sample or refer to the [TensorRT-RTX OSS Python samples](<https://github.com/NVIDIA/TensorRT-RTX/tree/main/samples>) .
Define a helper class to hold model metadata:
class ModelData ( object ): INPUT_NAME = "data" INPUT_SHAPE = ( 1 , 1 , 28 , 28 ) OUTPUT_NAME = "prob" OUTPUT_SIZE = 10 DTYPE = trt . float32
Import the weights from the PyTorch MNIST model:
weights = mnist_model . get_weights ()
Create the logger, builder, and network:
TRT_LOGGER = trt . Logger ( trt . Logger . ERROR ) builder = trt . Builder ( TRT_LOGGER ) network = builder . create_network ( 0 )
Create the input tensor, specifying the name, datatype, and shape:
input_tensor = network . add_input ( name = ModelData . INPUT_NAME , dtype = ModelData . DTYPE , shape = ModelData . INPUT_SHAPE )
Add a convolution layer, specifying the inputs, number of output maps, kernel shape, weights, bias, and stride:
conv1_w = weights [ "conv1.weight" ] . cpu () . numpy () conv1_b = weights [ "conv1.bias" ] . cpu () . numpy () conv1 = network . add_convolution_nd ( input = input_tensor , num_output_maps = 20 , kernel_shape = ( 5 , 5 ), kernel = conv1_w , bias = conv1_b ) conv1 . stride_nd = ( 1 , 1 )
Add a pooling layer, specifying the inputs, pooling type, window size, and stride:
pool1 = network . add_pooling_nd ( input = conv1 . get_output ( 0 ), type = trt . PoolingType . MAX , window_size = ( 2 , 2 )) pool1 . stride_nd = trt . Dims2 ( 2 , 2 )
Add the next pair of convolution and pooling layers:
conv2_w = weights [ "conv2.weight" ] . cpu () . numpy () conv2_b = weights [ "conv2.bias" ] . cpu () . numpy () conv2 = network . add_convolution_nd ( pool1 . get_output ( 0 ), 50 , ( 5 , 5 ), conv2_w , conv2_b ) conv2 . stride_nd = ( 1 , 1 ) pool2 = network . add_pooling_nd ( conv2 . get_output ( 0 ), trt . PoolingType . MAX , ( 2 , 2 )) pool2 . stride_nd = trt . Dims2 ( 2 , 2 )
Add a Shuffle layer to reshape the tensor in preparation for matrix multiplication:
# Get the output from the previous pooling layer pool2_output = pool2 . get_output ( 0 ) batch = pool2_output . shape [ 0 ] mm_inputs = np . prod ( pool2_output . shape [ 1 :]) input_reshape = network . add_shuffle ( pool2_output ) input_reshape . reshape_dims = trt . Dims2 ( batch , mm_inputs )
Add a MatrixMultiply layer. The model exporter provided transposed weights, so specify kTRANSPOSE :
fc1_w = weights [ 'fc1.weight' ] . numpy () fc1_const = network . add_constant ( trt . Dims2 ( nbOutputs , mm_inputs ), fc1_w ) mm = network . add_matrix_multiply ( input_reshape . get_output ( 0 ), trt . MatrixOperation . NONE , fc1_const . get_output ( 0 ), trt . MatrixOperation . TRANSPOSE )
Add bias, which will broadcast across the batch dimension:
bias_const = network . add_constant ( trt . Dims2 ( 1 , nbOutputs ), weights [ "fc1.bias" ] . numpy ()) bias_add = network . add_elementwise ( mm . get_output ( 0 ), bias_const . get_output ( 0 ), trt . ElementWiseOperation . SUM )
Add the ReLU activation layer:
relu1 = network . add_activation ( input = bias_add . get_output ( 0 ), type = trt . ActivationType . RELU )
Add the final fully connected layer, and mark the output of this layer as the output of the entire network:
fc2_w = weights [ 'fc2.weight' ] . numpy () fc2_b = weights [ 'fc2.bias' ] . numpy () fc2 = add_matmul_as_fc ( network , relu1 . get_output ( 0 ), ModelData . OUTPUT_SIZE , fc2_w , fc2_b ) fc2 . get_output ( 0 ) . name = ModelData . OUTPUT_NAME network . mark_output ( tensor = fc2 . get_output ( 0 ))
The network representing the MNIST model has now been fully constructed. For instructions on how to build an engine and run inference with this network, refer to the Building a TensorRT-RTX Engine and Performing Inference sections.
For more information regarding layers, refer to the [TensorRT-RTX Operator documentation](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/_static/operators/index.html>) .
Importing a Model Using the ONNX Parser
Populate the network definition from an ONNX file using the ONNX parser.
Create an ONNX parser bound to the network and logger:
parser = trt . OnnxParser ( network , logger )
Parse the ONNX file and print any parser errors:
success = parser . parse_from_file ( model_path ) for idx in range ( parser . num_errors ): print ( parser . get_error ( idx )) if not success : raise RuntimeError ( f "Failed to parse ONNX model: { model_path } " )
Parsing failures leave the network definition incomplete, so stop here rather than continuing to the builder.
Building a TensorRT-RTX Engine
Configure the builder and serialize the optimized network to an engine buffer.
Create a build configuration:
config = builder . create_builder_config ()
This interface has many properties that control how TensorRT-RTX optimizes the network. One important property is the maximum workspace size. Layer implementations often require a temporary workspace, and this parameter limits the maximum size that any layer in the network can use. If insufficient workspace is provided, TensorRT-RTX may not be able to find an implementation for a layer.
Important
The default workspace limit is the total global memory of the device . Set an explicit limit whenever the builder shares the GPU with other work, such as building several engines on one device, building while an application is already running inference, or building on a display GPU.
Set the workspace memory pool limit:
config . set_memory_pool_limit ( trt . MemoryPoolType . WORKSPACE , 1 << 20 ) # 1 MiB
Build and serialize the engine:
serialized_engine = builder . build_serialized_network ( network , config )
Save the serialized engine to a file for later use:
with open ( "sample.engine" , "wb" ) as f : f . write ( serialized_engine )
Deserializing a TensorRT-RTX Engine
When you have a previously serialized optimized model and want to perform inference, create a runtime instance first.
Create the runtime with the same logger interface used during the build phase:
runtime = trt . Runtime ( logger )
TensorRT-RTX provides two main methods to deserialize an engine, each with its use case and benefits.
In-memory Deserialization Use this approach for smaller models or when holding the full engine in memory is acceptable.
Read the serialized engine file into a buffer:
with open ( "model.engine" , "rb" ) as f : model_data = f . read ()
Deserialize the engine from the buffer:
engine = runtime . deserialize_cuda_engine ( model_data )
Alternatively, deserialize directly from a serialized engine object returned by the builder:
serialized_engine = builder . build_serialized_network ( network , config ) engine = runtime . deserialize_cuda_engine ( serialized_engine )
IStreamReaderV2 Deserialization Use this approach for custom file handling, large models, or streaming. Reading the entire engine into a single buffer is optional because IStreamReaderV2 can supply data in chunks.
Implement trt.IStreamReaderV2 with read() and seek() methods for your storage backend.
Construct a reader and deserialize:
class StreamReaderV2 ( trt . IStreamReaderV2 ): def __init__ ( self , bytes ): trt . IStreamReaderV2 . __init__ ( self ) self . bytes = bytes self . len = len ( bytes ) self . index = 0 def read ( self , size , cudaStreamPtr ): assert self . index + size <= self . len data = self . bytes [ self . index : self . index + size ] self . index += size return data def seek ( self , offset , where ): if where == trt . SeekPosition . SET : new_index = offset elif where == trt . SeekPosition . CUR : new_index = self . index + offset elif where == trt . SeekPosition . END : new_index = self . len + offset else : return False if not 0 <= new_index <= self . len : return False self . index = new_index return True with open ( "model.engine" , "rb" ) as f : reader_v2 = StreamReaderV2 ( f . read ()) engine = runtime . deserialize_cuda_engine ( reader_v2 )
The trt.IStreamReaderV2 method reads the engine in chunks instead of requiring the whole serialized engine in host memory at once, so peak host memory during deserialization scales with the chunk size rather than the engine size. It is also the path that supports GPUDirect and weight streaming. The benefit grows with engine size; measure against your own engine to decide whether it is worth the extra implementation.
When choosing a deserialization method, consider your specific requirements:
For small models or simple use cases, in-memory deserialization is often sufficient.
For large models or when memory efficiency is crucial, consider using IStreamReaderV2 (C++) or trt.IStreamReaderV2 (Python).
If you need custom file handling or weight streaming capabilities, IStreamReaderV2 provides the necessary flexibility.
Checking Engine Compatibility
Engines you create might not always be compatible with all TensorRT-RTX runtimes or all NVIDIA GPUs, for example, if you constrain the compute capabilities of your engine using the [Compute Capability API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) or because the engine was [compiled with a different version of TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/release-notes.html#release-notes>) .
Use IRuntime::getEngineValidity() (C++) or Runtime.get_engine_validity() (Python) to check whether an engine is expected to work before you deserialize the full payload. This API inspects only the engine header, so it is faster and uses less memory than full deserialization.
For compatibility scenarios, diagnostics bitmasks, and complete C++ and Python examples, refer to [Compatibility Checks](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/engine-compatibility.html#compatibility-checks>) .
validity , diagnostics = runtime . get_engine_validity ( serialized_engine ) if validity == trt . EngineValidity . INVALID : raise RuntimeError ( "Engine is invalid." )
Refer to the [sample code](<https://github.com/NVIDIA/TensorRT-RTX/blob/main/samples/apiUsage/python/api_usage.py>) for a complete example.
Performing Inference
The TensorRT-RTX engine holds the optimized model. Manage per-inference activation state with an execution context.
Create an execution context from the deserialized engine:
context = engine . create_execution_context ()
An engine can have multiple execution contexts, allowing one set of weights to be used for multiple overlapping inference tasks. A single context must not be used concurrently from multiple threads; refer to [Thread Safety](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#thread-safety>) .
Allocate GPU buffers for every input and output tensor. This example uses PyTorch, but the official CUDA Python bindings, cuPy, and Numba work the same way, because TensorRT-RTX only needs the device pointer:
import torch TORCH_DTYPES = { trt . DataType . FLOAT : torch . float32 , trt . DataType . HALF : torch . float16 , trt . DataType . INT32 : torch . int32 , trt . DataType . INT8 : torch . int8 , } buffers = {} for i in range ( engine . num_io_tensors ): name = engine . get_tensor_name ( i ) shape = tuple ( context . get_tensor_shape ( name )) dtype = TORCH_DTYPES [ engine . get_tensor_dtype ( name )] buffers [ name ] = torch . empty ( shape , dtype = dtype , device = "cuda" )
Bind each buffer to the context using tensor names:
for name , tensor in buffers . items (): context . set_tensor_address ( name , tensor . data_ptr ())
The buffers must stay alive and at the same device address until inference completes. Refer to [Buffer Lifetime](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#buffer-lifetime>) .
Create or obtain a CUDA stream pointer for asynchronous execution. For example, with PyTorch use torch.cuda.Stream().cuda_stream ; with Polygraphy streams use the ptr attribute; or create a stream with the [CUDA Python bindings](<https://nvidia.github.io/cuda-python/cuda-core/latest/generated/cuda.core.Device.html#cuda.core.Device.create_stream>) .
Enqueue inference on the stream:
context . execute_async_v3 ( stream_ptr )
It is common to enqueue asynchronous transfers ( cudaMemcpyAsync() ) before and after the kernels to move data to or from the GPU.
Synchronize the stream (or wait on a CUDA event) to determine when inference and asynchronous transfers complete. For PyTorch or Polygraphy streams, call stream.synchronize() . For CUDA Python binding streams, call [cuda.core.Device.sync](<https://nvidia.github.io/cuda-python/cuda-core/latest/generated/cuda.core.Device.html#cuda.core.Device.sync>) .
