Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/timing-cache-migration.html

Timing Cache Migration
ITimingCache and related builder APIs persist from TensorRT but does not affect engines built by TensorRT-RTX. Using them forces a GPU query during ahead-of-time compilation, which prevents CPU-only AOT builds. These APIs were deprecated in TensorRT-RTX 1.2 and will be removed in a future release.
Table 6 Timing cache APIs in TensorRT-RTX API / behavior
Effect in TensorRT-RTX
Recommendation
ITimingCache at build time
No impact on the serialized engine; requires a GPU during AOT
Do not use; remove from ported TensorRT code
[Runtime cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>)
Stores JIT-compiled kernels for faster startup
Use runtime cache instead for cross-run performance
For porting notes, refer to the [Porting Guide for TensorRT Applications](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/porting.html#porting>) . For runtime cache setup, refer to [Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) .
