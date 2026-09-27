Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/dynamic-shapes-basics.html

Dynamic Shapes: Core Concepts
This page covers the APIs and workflows you use most often when inputs or outputs can change size at runtime. If you are new to dynamic shapes, start with [Working with Dynamic Shapes](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-dynamic-shapes.html#work-with-dynamic-shapes>) for the end-to-end build and inference sequence ( -1 placeholders, optimization profiles, setInputShape() , and enqueueV3() ).
Here you will learn how to:
Declare runtime dimensions at build time and set concrete shapes on the execution context
Name dimensions and express equality constraints between them
Define optimization profiles so the builder can tune kernels across a permitted size range
Allocate memory when output sizes depend on the inputs you supply at runtime
For shape tensors, layer-level restrictions, and two-phase execution details, refer to [Dynamic Shapes: Advanced Topics](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/dynamic-shapes-advanced.html#layer-extensions>) .
Specifying Runtime Dimensions
When building a network, use -1 to denote a runtime dimension for an input tensor. For example, to create a 3D input tensor named foo where the last two dimensions are specified at runtime, and the first dimension is fixed at build time, issue the following.
C++ networkDefinition -> addInput ( "foo" , DataType :: kFLOAT , Dims3 ( 3 , -1 , -1 ))
Python network_definition . add_input ( "foo" , trt . float32 , ( 3 , - 1 , - 1 ))
After choosing an optimization profile, you must set the input dimensions at run time (refer to Optimization Profiles ). Let the input have dimensions [3,150,250] . After setting an optimization profile for the previous example, you would call:
C++ context -> setInputShape ( "foo" , Dims { 3 , { 3 , 150 , 250 }})
Python context . set_input_shape ( "foo" , ( 3 , 150 , 250 ))
At runtime, asking the engine for binding dimensions returns the same dimensions used to build the network, meaning you get a -1 for each runtime dimension. For example:
C++ engine -> getTensorShape ( "foo" )
Returns a Dims with dimensions {3, -1, -1} .
Python engine . get_tensor_shape ( "foo" )
Returns (3, -1, -1) .
To get the actual dimensions, which are specific to each execution context, query the execution context:
C++ context -> getTensorShape ( "foo" )
Returns a Dims with dimensions {3, 150, 250} .
Python context . get_tensor_shape ( "foo" )
Returns (3, 150, 250) .
The return value of setInputShape for input only indicates consistency for the optimization profile set for that input. After all input binding dimensions are specified, you can check whether the entire network is consistent with the dynamic input shapes by querying the dimensions of the output bindings of the network. Here is an example that retrieves the dimensions of an output named bar :
C++ 1 nvinfer1 :: Dims outDims = context -> getTensorShape ( "bar" ); 2 3 if ( outDims . nbDims == -1 ) { 4 gLogError << "Invalid network output, this might be caused by inconsistent input shapes." << std :: endl ; 5 // stop inference 6 }
Python 1 out_dims = context . get_tensor_shape ( "bar" ) 2 3 if - 1 in out_dims : 4 print ( "Invalid network output, this might be caused by inconsistent input shapes." ) 5 # stop inference
If a dimension k is data-dependent, for example, outDims.d[k] (C++) or out_dims[k] (Python) will be -1 . For more information on such outputs, refer to the Dynamically Shaped Output section.
Named Dimensions
Both constant and runtime dimensions can be named. Naming dimensions provides two benefits:
For runtime dimensions, error messages use the dimension’s name. For example, if an input tensor foo has dimensions [n,10,m] , it is more helpful to get an error message about m instead of (#2 (SHAPE foo)) .
Dimensions with the same name are implicitly equal, which can help the optimizer generate a more efficient engine and diagnose mismatched dimensions at runtime. For example, suppose two inputs have dimensions [n,10,m] and [n,13] . In that case, the optimizer knows the lead dimensions are always equal, and accidentally using the engine with mismatched values for n will be reported as an error.
You can use the same name for constant and runtime dimensions as long as they are always equal.
The following syntax examples set the name of the third dimension of the tensor to m .
C++ tensor . setDimensionName ( 2 , "m" )
Python tensor . set_dimension_name ( 2 , "m" )
There are corresponding methods to get a dimensions name:
C++ tensor . getDimensionName ( 2 ) // returns the name of the third dimension of the tensor, or nullptr if it does not have a name.
Python tensor . get_dimension_name ( 2 ) # returns the name of the third dimension of the tensor, or None if it does not have a name.
When the input network is imported from an ONNX file, the ONNX parser automatically sets the dimension names using the names in the ONNX file. Therefore, if two dynamic dimensions are expected to be equal at runtime, specify the same name for these dimensions when exporting the ONNX file.
Dimension Constraint using IAssertionLayer
Sometimes, two dynamic dimensions are not known to be equal statically but are guaranteed equal at runtime. Letting TensorRT-RTX know they are equal can help it build a more efficient engine. There are two ways to convey the equality constraint to TensorRT-RTX:
Give the dimensions the same name as described in the Named Dimensions section.
Use IAssertionLayer to express the constraint. This technique is more general since it can convey trickier equalities.
For example, if the first dimension of tensor A is guaranteed to be one more than the first dimension of tensor B, then the constraint can be established by:
C++ // Assumes A and B are ITensor* and n is a INetworkDefinition&. auto shapeA = n . addShape ( * A ) -> getOutput ( 0 ); auto firstDimOfA = n . addSlice ( * shapeA , Dims { 1 , { 0 }}, Dims { 1 , { 1 }}, Dims { 1 , { 1 }}) -> getOutput ( 0 ); auto shapeB = n . addShape ( * B ) -> getOutput ( 0 ); auto firstDimOfB = n . addSlice ( * shapeB , Dims { 1 , { 0 }}, Dims { 1 , { 1 }}, Dims { 1 , { 1 }}) -> getOutput ( 0 ); static int32_t const oneStorage { 1 }; auto one = n . addConstant ( Dims { 1 , { 1 }}, Weights { DataType :: kINT32 , & oneStorage , 1 }) -> getOutput ( 0 ); auto firstDimOfBPlus1 = n . addElementWise ( * firstDimOfB , * one , ElementWiseOperation :: kSUM ) -> getOutput ( 0 ); auto areEqual = n . addElementWise ( * firstDimOfA , * firstDimOfBPlus1 , ElementWiseOperation :: kEQUAL ) -> getOutput ( 0 ); n . addAssertion ( * areEqual , "oops" );
Python # Assumes `a` and `b` are ITensors and `n` is an INetworkDefinition shape_a = n . add_shape ( a ) . get_output ( 0 ) first_dim_of_a = n . add_slice ( shape_a , ( 0 , ), ( 1 , ), ( 1 , )) . get_output ( 0 ) shape_b = n . add_shape ( b ) . get_output ( 0 ) first_dim_of_b = n . add_slice ( shape_b , ( 0 , ), ( 1 , ), ( 1 , )) . get_output ( 0 ) one = n . add_constant (( 1 , ), np . ones (( 1 , ), dtype = np . int32 )) . get_output ( 0 ) first_dim_of_b_plus_1 = n . add_elementwise ( first_dim_of_b , one , trt . ElementWiseOperation . SUM ) . get_output ( 0 ) are_equal = n . add_elementwise ( first_dim_of_a , first_dim_of_b_plus_1 , trt . ElementWiseOperation . EQUAL ) . get_output ( 0 ) n . add_assertion ( are_equal , "oops" )
If the dimensions violate the assertion at runtime, TensorRT-RTX will throw an error.
Optimization Profiles
An optimization profile describes a range of dimensions for each network input. You must create at least one optimization profile at build time when using runtime dimensions. Two profiles can specify disjoint or overlapping ranges.
For example, one profile might specify a minimum size of [3,100,200] , a maximum size of [3,200,300] , and optimization dimensions of [3,150,250] , while another profile might specify min, max, and optimization dimensions of [3,200,100] , [3,300,400] , and [3,250,250] .
Table 8 Optimization profile selectors (per runtime input) Selector
Meaning
Example for input foo (profile 0)
MIN ( kMIN )
Smallest runtime shape the engine must support
[3, 100, 200]
OPT ( kOPT )
Shape the builder tunes tactics for (often typical production size)
[3, 150, 250]
MAX ( kMAX )
Largest runtime shape the engine must support
[3, 200, 300]
At runtime, choose a profile index whose MIN–MAX range contains your actual input dimensions, then call setInputShape() for each dynamic input.
Table 9 Optimization profile comparison (example input foo ) Profile index
MIN dimensions
OPT dimensions
MAX dimensions
Range span (H × W)
Notes
0
[3, 100, 200]
[3, 150, 250]
[3, 200, 300]
100–200 × 200–300
Default profile; moderate spatial sizes
1
[3, 200, 100]
[3, 250, 250]
[3, 300, 400]
200–300 × 100–400
Overlapping or disjoint with profile 0; different aspect-ratio envelope
Warning
The memory usage for different profiles can change dramatically based on the dimensions specified by the min, max, and opt parameters. Some operations have kernels that only work for MIN=OPT=MAX , so when these values differ, the kernel is disabled. Setting min and max far apart can cause out-of-memory failures at runtime.
To create an optimization profile, first construct an IOptimizationProfile . Then, set the min, optimization, and max dimensions and add them to the network configuration. The shapes defined by the optimization profile must define valid input shapes for the network. Here are the calls for the first profile mentioned previously for an input foo :
C++ 1 IOptimizationProfile * profile = builder . createOptimizationProfile (); 2 profile -> setDimensions ( "foo" , OptProfileSelector :: kMIN , Dims3 ( 3 , 100 , 200 )); 3 profile -> setDimensions ( "foo" , OptProfileSelector :: kOPT , Dims3 ( 3 , 150 , 250 )); 4 profile -> setDimensions ( "foo" , OptProfileSelector :: kMAX , Dims3 ( 3 , 200 , 300 )); 5 6 config -> addOptimizationProfile ( profile )
Python 1 profile = builder . create_optimization_profile (); 2 profile . set_shape ( "foo" , ( 3 , 100 , 200 ), ( 3 , 150 , 250 ), ( 3 , 200 , 300 )) 3 config . add_optimization_profile ( profile )
At runtime, you must set an optimization profile before setting input dimensions. Profiles are numbered in the order they were added, starting at 0 . Each execution context must use a separate optimization profile.
To choose the first optimization profile in the example, use:
C++ context -> setOptimizationProfileAsync ( 0 , stream )
Python context . set_optimization_profile_async ( 0 , stream )
The provided stream argument should be the same CUDA stream that will be used for the subsequent enqueueV3() invocation in this context. This ensures that the context executions happen after the optimization profile setup.
Suppose the associated TensorRT-RTX engine has dynamic inputs. In that case, the optimization profile must be set at least once with a unique profile index that is not used by other execution contexts and that is not destroyed. For the first execution context created for an engine, profile 0 is implicitly chosen.
setOptimizationProfileAsync() can be called to switch between profiles. It must be called after any enqueueV3() operations finish in the current context. (TensorRT-RTX does not support the deprecated enqueue() / enqueueV2() APIs from earlier TensorRT-RTX releases; use enqueueV3() exclusively.) When multiple execution contexts run concurrently, it can switch to a formerly used profile already released by another execution context with different dynamic input dimensions.
Note
setOptimizationProfileAsync() replaces the now deprecated setOptimizationProfile() API. Using setOptimizationProfile() to switch between optimization profiles can cause GPU memory copy operations in subsequent enqueueV3() operations. To avoid these, use setOptimizationProfileAsync() instead.
Dynamically Shaped Output
If the output of a network has a dynamic shape, several strategies are available to allocate the output memory.
If the dimensions of the output are computable from the dimensions of inputs, use IExecutionContext::getTensorShape() to get the dimensions of the output after providing the dimensions of the input tensors and [input shape tensors](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/dynamic-shapes-advanced.html#shape-tensor-io>) . Use the IExecutionContext::inferShapes() method to check if you forgot to supply the necessary information.
Otherwise, if the dimensions of the output are not computable in advance or calling enqueueV3 , associate an IOutputAllocator with the output. More specifically:
Derive your allocator class from IOutputAllocator .
Override the reallocateOutput and notifyShape methods. TensorRT-RTX calls the first when it needs to allocate the output memory and the second when it knows the output dimensions.
Here is an example derived class:
class MyOutputAllocator : nvinfer1 :: IOutputAllocator { public : void * reallocateOutput ( char const * tensorName , void * currentMemory , uint64_t size , uint64_t alignment ) override { // Allocate the output. Remember it for later use. outputPtr = /* depends on strategy, as discussed later …*/ return outputPtr ; } void notifyShape ( char const * tensorName , Dims const & dims ) { // Remember output dimensions for later use. outputDims = dims ; } // Saved dimensions of the output Dims outputDims {}; // nullptr if memory could not be allocated void * outputPtr { nullptr }; };
The following is an example of how it might be used:
std :: unordered_map < std :: string , MyOutputAllocator > allocatorMap ; for ( const char * name : /* names of outputs */ ) { Dims extent = context -> getTensorShape ( name ); void * ptr ; if ( engine -> getTensorLocation ( name ) == TensorLocation :: kDEVICE ) { if ( /* extent.d contains -1 */ ) { auto allocator = std :: make_unique < MyOutputAllocator > (); context -> setOutputAllocator ( name , allocator . get ()); allocatorMap . emplace ( name , std :: move ( allocator )); } else { ptr = /* allocate device memory per extent and format */ } } else { ptr = /* allocate cpu memory per extent and format */ } context -> setTensorAddress ( name , ptr ); }
Several strategies can be used for implementing reallocateOutput :
Output allocation strategies A–E A
Defer allocation until the size is known. Do not call IExecutionContext::setTensorAddress , or call it with a nullptr for the tensor address.
B
Preallocate enough memory based on what IExecutionContext::getMaxOutputSize reports as an upper bound. This guarantees that the engine will not fail due to insufficient output memory, but the upper bound may be so high that it is useless.
C
If you have preallocated enough memory based on experience, use IExecutionContext::setTensorAddress to tell TensorRT-RTX about it. If the tensor does not fit, make reallocateOutput return nullptr , which will cause the engine to fail gracefully.
D
Preallocate memory as in C , but have reallocateOutput return a pointer to a bigger buffer if there is a fit problem. This increases the output buffer as needed.
E
Defer allocation until the size is known, like A . Then, attempt to recycle that allocation in subsequent calls until a bigger buffer is requested, and then increase it like in D .
The following is an example derived class that implements E :
class FancyOutputAllocator : nvinfer1 :: IOutputAllocator { public : void reallocateOutput ( char const * tensorName , void * currentMemory , uint64_t size , uint64_t alignment ) override { if ( size > outputSize ) { // Need to reallocate cudaFree ( outputPtr ); outputPtr = nullptr ; outputSize = 0 ; if ( cudaMalloc ( & outputPtr , size ) == cudaSuccess ) { outputSize = size ; } } // If the cudaMalloc fails, outputPtr=nullptr, and engine // gracefully fails. return outputPtr ; } void notifyShape ( char const * tensorName , Dims const & dims ) { // Remember output dimensions for later use. outputDims = dims ; } // Saved dimensions of the output tensor Dims outputDims {}; // nullptr if memory could not be allocated void * outputPtr { nullptr }; // Size of allocation pointed to by output uint64_t outputSize { 0 }; ~ FancyOutputAllocator () override { cudaFree ( outputPtr ); } };
TensorRT-RTX internally allocates memory asynchronously in the device’s current memory pool for networks with data-dependent shapes.
Tip
If the current device memory pool does not have a release threshold set, performance degradation between runs can occur as the memory is returned to the operating system upon stream synchronization. Provide the TensorRT-RTX runtime with a custom IGpuAllocator with a custom memory pool, or set a release threshold on the pool. More information about setting the release threshold can be found in [Retaining Memory in the Pool](<https://developer.nvidia.com/blog/using-cuda-stream-ordered-memory-allocator-part-1/#retaining_memory_in_the_pool>) and the [Code Migration Guide](<https://developer.nvidia.com/blog/using-cuda-stream-ordered-memory-allocator-part-2/#code_migration_guide>) .
Legacy binding indices (enqueueV2 only) TensorRT-RTX supports enqueueV3 exclusively (not the deprecated enqueueV2 APIs from earlier TensorRT releases). If you use enqueueV3 , you can skip this section because name-based methods such as IExecutionContext::setTensorAddress do not expect a profile suffix.
Each profile has separate binding indices in an engine built from multiple profiles. The names of I/O tensors for the K profile have [profile K] appended to them, with K written in decimal. For example, if the INetworkDefinition had the name foo , and bindingIndex refers to that tensor in the optimization profile with index 3 , engine.getBindingName(bindingIndex) returns foo [profile 3] .
Likewise, if using ICudaEngine::getBindingIndex(name) to get the index for a profile K beyond the first profile ( K=0 ), append [profile K] to the name used in the INetworkDefinition . For example, if the tensor was called foo in the INetworkDefinition , engine.getBindingIndex("foo [profile 3]") returns the binding index of Tensor foo in optimization profile 3 .
Always omit the suffix for K=0 .
