Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/operators.html

TensorRT-RTX Operators
In TensorRT-RTX, operators represent distinct flavors of mathematical and programmatic operations. The following sections describe every operator that TensorRT-RTX supports. The minimum workspace required by TensorRT-RTX depends on the operators used by the network.
Workspace Size : The default workspace size is 16 MB. For most networks, this is sufficient. However, complex networks with many operators may require a larger workspace. A suggested minimum build-time setting is 16 MB, but you can increase the workspace pool limit by calling IBuilderConfig::setMemoryPoolLimit(MemoryPoolType::kWORKSPACE, poolSize) if needed. Regardless of the workspace pool limit provided to the builder, TensorRT-RTX will allocate at runtime no more than the workspace it actually requires.
Interface - [TensorRT-RTX Operators](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/_static/operators/index.html>)
See also
[Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>)
Supported data types and hardware compatibility per operator.
[C++ API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/c-api.html#c-api>)
Programmatic access to operators through the C++ interface.
[Python API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/python-api.html#python-api>)
Programmatic access to operators through the Python interface.
[Work with Quantized Types](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-quantized-types.html#quantized-types>)
Operator support for reduced-precision types (INT4, INT8, FP4, FP8, BF16).
