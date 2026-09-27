Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/best-practices.html

Best Practices
This guide presents the two pillars of TensorRT-RTX performance work: benchmarking (measuring what your model actually does) and optimization (changing what your model does so it runs faster). Treat them as a feedback loop: measure first, optimize, then measure again to confirm the change had the impact you expected.
Benchmarking
Benchmarking is how you turn a TensorRT-RTX model into trustworthy numbers (latency, throughput, per-layer cost) that you can compare across builds, hardware, and configurations. The [Performance Benchmarking](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/benchmarking.html#performance-benchmarking>) chapter walks through:
Running the tensorrt_rtx executable with ONNX models to measure throughput and latency
Getting per-layer runtime and layer information with --dumpProfile and --dumpLayerInfo
Profiling with NVIDIA Nsight Systems and NVTX ranges marked by TensorRT-RTX
Controlling the hardware and software environment (GPU clocks, power and thermal throttling, synchronization mode) so your numbers are stable and reproducible
Without a stable measurement baseline, every optimization you try is a guess. Start here.
Optimization
Once you trust your numbers, the [Optimizing TensorRT-RTX Performance](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/optimization.html#optimize-performance>) chapter covers the techniques you can apply to push them further:
Choosing network precision and quantized ONNX models supported on your GPU
Dynamic shape specialization , runtime caching , and warmup for JIT-compiled kernels
Increasing parallelism with batching , CUDA graphs , and within-inference multi-streaming
Detecting enqueue-bound workloads and capturing CUDA graphs via IRuntimeConfig
Each section is independent, so you can jump straight to the techniques most relevant to the bottlenecks your benchmarks surfaced.
See also
[How TensorRT-RTX Works](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#works>)
Object lifetimes, memory, threading, and engine compatibility for production applications.
[Performance Benchmarking](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/benchmarking.html#performance-benchmarking>)
Measure latency, throughput, and per-layer cost with tensorrt_rtx and profiling tools.
[Optimizing TensorRT-RTX Performance](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/optimization.html#optimize-performance>)
Apply precision, caching, batching, and CUDA graph techniques after you have a baseline.
