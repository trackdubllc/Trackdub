Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/benchmarking.html

Performance Benchmarking
This guide consolidates everything you need to measure TensorRT-RTX inference performance using the tensorrt_rtx command-line tool, advanced timing techniques, and the hardware and software factors that influence the numbers you collect.
What You Will Learn
How to establish an inference performance baseline with the tensorrt_rtx command-line tool
How to inspect per-layer timing and layer information to identify bottlenecks
How to profile inference with NVTX tracing and NVIDIA Nsight Systems
How GPU clocks, power and thermal throttling, and synchronization modes affect benchmark results
See also
[Optimizing TensorRT-RTX Performance](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/optimization.html#optimize-performance>)
After you’ve established a measurement baseline, apply optimization techniques to improve it.
Measure Inference Performance
If your model is already in the ONNX format, the tensorrt_rtx tool can measure its performance directly. In this example, we will use the [ResNet-50 v1 ONNX model](<https://github.com/onnx/models/blob/main/validated/vision/classification/resnet/model/resnet50-v1-12.onnx>) from the ONNX model zoo to showcase how to use the tensorrt_rtx executable to measure its performance with batch size 4 :
tensorrt_rtx --onnx=resnet50-v1-12.onnx --shapes=data:4x3x224x224
Where:
The --onnx flag specifies the path to the ONNX file.
The --shapes flag specifies the input tensor shapes.
The value for the --shapes flag is in the format of name1:shape1,name2:shape2,.. . You can get the input tensor names and shape profiles by visualizing the ONNX model using tools like [Netron](<https://github.com/lutzroeder/netron>) or by running a [Polygraphy](<https://docs.nvidia.com/deeplearning/tensorrt/latest/_static/polygraphy/index.html>) model inspection.
The tensorrt_rtx --help command provides a full description of all available flags.
Note
The tensorrt_rtx executable excludes host-to-device and device-to-host transfers, uses spin-wait synchronization, and enables whole-graph RTX CUDA capture by default. This provides an optimized and representative inference baseline. Use --includeDataTransfers , --noSpinWait , or --rtxCudaGraphStrategy=disable when the alternative behavior matches what you need to measure.
After running the basic tensorrt_rtx command above, the tool will:
Parse your ONNX file
Build a TensorRT-RTX engine
Measure the performance of this engine
Print a performance summary
Listing 11 Expected summary
[I] === Performance summary ===
[I] Throughput: 1133.1 qps
[I] Latency: min = 0.877441 ms, max = 0.886963 ms, mean = 0.881485 ms,
median = 0.881592 ms, percentile(90%) = 0.883606 ms,
percentile(95%) = 0.883789 ms, percentile(99%) = 0.885010 ms
[I] Enqueue Time: min = 0.004639 ms, max = 0.020630 ms, mean = 0.005356 ms,
median = 0.005249 ms, percentile(90%) = 0.005859 ms,
percentile(95%) = 0.006104 ms, percentile(99%) = 0.007324 ms
[I] GPU Compute Time: min = 0.877441 ms, max = 0.886963 ms,
mean = 0.881485 ms, median = 0.881592 ms,
percentile(90%) = 0.883606 ms, percentile(95%) = 0.883789 ms,
percentile(99%) = 0.885010 ms
Use the metric that matters most to your use case:
Throughput is the number of inference executions completed per second.
Latency is H2D transfer time + GPU Compute Time + D2H transfer time. With data transfers disabled by default, it equals GPU Compute Time.
Enqueue Time is the host time required to enqueue an inference query.
GPU Compute Time is the time spent executing one inference workload on the GPU, excluding I/O data transfers.
Per-Layer Performance and Layer Information
Note
Advanced Topic : This section covers detailed profiling for identifying performance bottlenecks. Most users can skip this initially.
Use per-layer profiling to identify which optimized layers contribute most to GPU execution time and inspect their layer metadata. This helps you determine how much latency each layer contributes to the overall inference and identify performance bottlenecks.
The following command prints per-layer performance and layer information for the ResNet-50 ONNX model:
tensorrt_rtx --onnx=resnet50-v1-12.onnx \
--shapes=data:4x3x224x224 \
--profilingVerbosity=detailed \
--dumpLayerInfo \
--dumpProfile
Where:
--profilingVerbosity=detailed includes detailed layer metadata.
--dumpLayerInfo prints the optimized engine layer information.
--dumpProfile runs a separate profiling pass and prints per-layer runtime.
For TensorRT-RTX, the tool disables RTX CUDA Graphs for this profiling pass automatically so that it can collect individual layer timings.
By default, tensorrt_rtx warms up for at least 200 ms and runs inference for at least 10 iterations or at least 3 seconds, whichever is longer. Refer to tensorrt_rtx --help for a detailed explanation about how to customize this and other tensorrt_rtx flags.
Abridged example profile output:
[I] === Profile (2929 iterations) ===
[I] Time(ms) Avg.(ms) Median(ms) Time(%) Layer
[I] 15.58 0.0053 0.0053 0.5 __myl_Move_myl0_0
[I] 116.78 0.0399 0.0401 3.7 __myl_CorrMulAddRelu_myl0_1
[I] 21.37 0.0073 0.0072 0.7 resnetv17_pool0_fwd_U3_myl0_2
[I] ... ... ... ... ...
[I] 3178.92 1.0853 1.0852 100.0 Total
Start with the layers that have the largest Time (%) values when investigating performance bottlenecks. Cross-reference their names with the --dumpLayerInfo output to correlate optimized engine layers with source ONNX nodes.
CUDA Profiling Tools
Note
Advanced Topic : Use CUDA profiling tools only when you need deep kernel-level analysis. The tensorrt_rtx tool is sufficient for most performance measurements.
The recommended CUDA profiler is [NVIDIA Nsight Systems](<https://developer.nvidia.com/nsight-systems>) . You can use this profiler on any CUDA program to report timing information about the kernels launched during execution, data movement between host and device, and the CUDA API calls used.
Nsight Systems can be configured to report timing information for only a portion of the program’s execution or to report traditional CPU sampling profile information and GPU information.
The basic usage of Nsight Systems is first to run the command nsys profile -o <OUTPUT> <INFERENCE_COMMAND> , then open the generated <OUTPUT>.nsys-rep file in the Nsight Systems GUI to visualize the captured profiling results.
Using the NVTX Tracing in Nsight Systems
Tracing enables the [NVIDIA Tools Extension SDK (NVTX)](<https://nvidia.github.io/NVTX/>) , a C-based API for marking events and ranges in your applications. It allows Nsight Compute and Nsight Systems to collect data generated by TensorRT-RTX applications.
Decoding the kernel names back to layers in the original network can be complicated. Because of this, TensorRT-RTX uses NVTX to mark a range for each layer, allowing the CUDA profilers to correlate each layer with the kernels called to implement it. In TensorRT-RTX, NVTX helps to correlate the runtime engine layer execution with CUDA kernel calls. Nsight Systems supports collecting and visualizing these events and ranges on the timeline. Nsight Compute also supports collecting and displaying the state of all active NVTX domains and ranges in a given thread when the application is suspended.
In TensorRT-RTX, each layer can launch one or more kernels to perform its operations. The exact kernels launched depend on the optimized network and the hardware present. Depending on the builder’s choices, multiple additional operations that reorder data may be interspersed with layer computations; these reformat operations may be implemented as device-to-device memory copies or custom kernels.
Controlling the Level of Details in NVTX Tracing
By default, TensorRT-RTX uses ProfilingVerbosity::kLAYER_NAMES_ONLY (or ProfilingVerbosity.LAYER_NAMES_ONLY in Python), which shows only layer names in the NVTX markers. Users can control the level of details by setting the ProfilingVerbosity in the IBuilderConfig when the engine is built. For example, to disable NVTX tracing, set the ProfilingVerbosity to kNONE :
C++ 1 builderConfig -> setProfilingVerbosity ( ProfilingVerbosity :: kNONE );
Python 1 import tensorrt_rtx as trt 2 builder_config . profiling_verbosity = trt . ProfilingVerbosity . NONE
On the other hand, you can choose to allow TensorRT-RTX to print more detailed layer information in the NVTX markers, including input and output dimensions, operations, parameters, tactic numbers, and so on, by setting the ProfilingVerbosity to kDETAILED :
C++ 1 builderConfig -> setProfilingVerbosity ( ProfilingVerbosity :: kDETAILED );
Python 1 import tensorrt_rtx as trt 2 builder_config . profiling_verbosity = trt . ProfilingVerbosity . DETAILED
Note
Enabling detailed NVTX markers increases the latency of enqueueV3() calls and could result in a performance drop if the performance depends on the latency of enqueueV3() calls.
Running Nsight Systems with tensorrt_rtx
The following example code shows how to gather Nsight Systems profiles using the tensorrt_rtx command.
Build the engine:
tensorrt_rtx --onnx=resnet50-v1-12.onnx \
--shapes=data:4x3x224x224 \
--profilingVerbosity=detailed \
--saveEngine=resnet50-v1-12.plan \
--skipInference
Profile 50 iterations of inference:
nsys profile -o resnet50_profile \
--capture-range=cudaProfilerApi \
tensorrt_rtx --loadEngine=resnet50-v1-12.plan \
--warmUp=0 --duration=0 --iterations=50
Note
To view individual kernel times when tracing with Nsight Systems, use --rtxCudaGraphStrategy=disable .
For more information on Nsight Systems traces for TensorRT-RTX, refer to the [Performance Profiling Tools](<https://docs.nvidia.com/deeplearning/tensorrt/latest/performance/benchmarking.html#performance-profiling-tools>) document.
Optional: Enabling GPU Metrics Sampling in Nsight Systems
On discrete GPU systems, add the --gpu-metrics-device all flag to the nsys command to sample GPU metrics, including GPU clock frequencies, DRAM bandwidth, Tensor Core utilization, and more. If the flag is added, these GPU metrics appear in the Nsight Systems web interface.
Hardware/Software Environment for Performance Measurements
Note
This section covers hardware and software configuration for consistent performance measurements. Start here only if you need reproducible benchmarking or are investigating performance variations.
Performance measurements are influenced by many factors, including hardware environment differences like the machine’s cooling capability and software environment differences like GPU clock settings. This section summarizes items that can affect performance measurements.
Table 17 Hardware environment factors during benchmarking Factor
Symptom
Mitigation
Floating GPU clock
Throughput varies between runs
Lock clock with nvidia-smi -lgc for stability, or accept floating clock for peak average performance
Power throttling
Clock drops after sustained load; short benchmarks look faster than steady-state
Use tensorrt_rtx (minimal gaps between kernels); avoid idle gaps between inferences in custom apps
Thermal throttling
Temperature rises until ~85 °C, then clocks drop
Improve cooling; monitor with nvidia-smi dmon
Input value distribution
Zeros/NaNs reduce power vs realistic activations
Use representative inputs; tensorrt_rtx defaults to random data
GPU Monitoring
While measuring performance, we recommend that you record and monitor the GPU status in parallel to the inference workload. Having the monitoring data allows you to identify possible root causes when you observe unexpected performance measurement results.
Before the inference starts, issue the nvidia-smi -q command to get detailed information on the GPU, including the product name, power cap, clock settings, and so on. Then, while the inference workload is running, run the nvidia-smi dmon -s pcu -f <FILE> -c <COUNT> command in parallel to print out GPU clock frequencies, power consumption, temperature, and utilization to a file. Issue nvidia-smi dmon --help for more options about the nvidia-smi device monitoring tool.
GPU Clock Locking
By default, the GPU clock frequency is floating, meaning it sits idle when there is no active workload and boosts the boost clock frequency when the workload starts. This is usually the desired behavior since it allows the GPU to generate less heat at idle and to run at maximum speed when there is an active workload.
Alternatively, you can lock the clock at a specific frequency by calling the nvidia-smi -lgc <freq> command (and conversely, you can let the clock float again with the nvidia-smi -rgc command). Refer to the [NVIDIA System Management Interface (nvidia-smi)](<https://docs.nvidia.com/deploy/nvidia-smi/index.html>) documentation for full command reference.
Note
On Linux, sudo may be required as a prefix to the command, and on Windows, the command prompt may require administrator privileges.
The nvidia-smi -q -d SUPPORTED_CLOCKS command can find the supported clock frequencies. After the clock frequency is locked, it should stay at that frequency unless power or thermal throttling occurs, which will be explained in the next sections. When the throttling kicks in, the device behaves like the clock floats.
Running TensorRT-RTX workloads with floating clocks or with throttling taking place can lead to unstable performance measurements across inferences because every CUDA kernel can run at slightly different clock frequencies, depending on which frequency the driver boosts or throttles the clock to at that moment. On the other hand, running TensorRT-RTX workloads with locked clocks allows more consistent performance measurements. Still, the average performance will not be as good as when the clock is floating or is locked at maximum frequency with throttling taking place.
There is no definite recommendation on whether the clock should be locked or which clock frequency to lock the GPU while running TensorRT-RTX workloads. It depends on whether the stable performance or the best average performance is desired.
GPU Power Throttling
Power throttling occurs when the average GPU power consumption reaches the power limit, which can be set by the sudo nvidia-smi -pl <power_cap> command. When this happens, the driver has to throttle the clock to a lower frequency to keep the average power consumption below the limit. The constantly changing clock frequencies may lead to unstable performance measurements if the measurements are taken within a short time, such as within 20ms.
Power throttling happens by design and is a natural phenomenon when the GPU clock is not locked or is locked at a higher frequency, especially for GPUs with lower power limits. To avoid performance variations caused by power throttling, you can lock the GPU clock at a lower frequency to stabilize the performance numbers. However, the average performance numbers will be lower than those with floating clocks or the clock locked at a higher frequency, even though power throttling would happen in this case.
Another issue with power throttling is that it can skew the performance numbers if there are gaps between inferences in your performance benchmarking applications. For example, if the application synchronizes at each inference, there will be periods when the GPU is idle between the inferences. The gaps cause the GPU to consume less power on average, so the clock is throttled less, and the GPU can run at higher clock frequencies on average. However, the throughput numbers measured this way are inaccurate because when the GPU is fully loaded with no gaps between inferences, the actual clock frequency will be lower, and the actual throughput will not reach the throughput numbers measured using the benchmarking application.
To avoid this, the tensorrt_rtx tool maximizes GPU execution by leaving nearly no gaps between GPU kernel executions so that it can measure the true throughput of a TensorRT-RTX workload. Therefore, if you observe performance gaps between your benchmarking application and what tensorrt_rtx reports, check whether power throttling and gaps between inferences are the cause.
Lastly, power consumption can depend on the activation values, causing different input performance measurements. For example, if all the network input values are set to zeros or NaNs, the GPU consumes less power than the inputs are normal values because of fewer bit-flips in DRAM and the L2 cache. To avoid this discrepancy, always use the input values that best represent the actual value distribution when measuring the performance. The tensorrt_rtx tool uses random input values by default, but you can specify the input using the --loadInputs flag.
GPU Thermal Throttling
Thermal throttling happens when the GPU temperature reaches a predefined threshold (around 85 degrees Celsius for most GPUs) and the driver throttles the clock to a lower frequency to prevent the GPU from overheating. You can identify this by observing the temperature logged by the nvidia-smi dmon command gradually increase while the inference workload runs until it approaches the temperature threshold and the clock frequency drops. Thermal throttling can be prevented or mitigated by improving cooling for the GPU.
A higher GPU temperature also leads to more leakage current in the circuits, which increases the power consumed by the GPU at a specific clock frequency. For GPUs more likely to be power throttled, poor cooling can lead to lower stabilized clock frequency with power throttling and worse performance, even if the GPU clocks have not been thermally throttled yet.
Synchronization Modes
If performance is measured with cudaStreamSynchronize() or cudaEventSynchronize() , synchronization overhead variations may lead to performance measurement variations. This section describes the causes of the variations and how to avoid them.
Table 18 CUDA stream and event synchronization modes Mechanism
How it is selected
Latency measurements
CPU / power trade-off
Blocking sync
cudaDeviceScheduleBlockingSync for streams; cudaEventBlockingSync for events
Less stable timing on some OSes
Host yields; lower CPU use: good when running heavy CPU work in parallel
Spin-wait
Default stream sync; cudaEventDefault for events
More stable, lower synchronization overhead
Host polls GPU; higher CPU use: preferred for reproducible benchmarks
When the blocking-sync mode is used, the host thread yields to another thread until the device work is done. When the spin-wait mode is used, the host thread polls until the device work is done. Choose blocking sync to save CPU resources; choose spin-wait for stable latency measurements.
Note
tensorrt_rtx uses spin-wait synchronization by default. Spin-wait generally provides lower and more stable synchronization overhead. Use --noSpinWait to use blocking synchronization. For a deeper discussion of the CPU, power, and latency tradeoffs, refer to [Synchronization Modes](<https://docs.nvidia.com/deeplearning/tensorrt/latest/performance/benchmarking.html#synch-modes>) .
