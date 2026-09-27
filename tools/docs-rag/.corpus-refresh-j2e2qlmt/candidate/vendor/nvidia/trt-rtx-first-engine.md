Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html

Build Your First Engine
Use this guide after you complete the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) to plan how you bundle engines in your application, support portability across RTX GPUs, and persist JIT artifacts for production launches.
If you have not run the minimum ONNX build and inference loop yet, start with the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) . It walks through the same ResNet-50 example in three commands. Return here when you are ready to ship an engine to end users.
Tip
Coming from TensorRT Enterprise? In Enterprise 11.x documentation, Build Your First Engine is the hands-on tutorial and Quick Start is the broader workflow menu. In TensorRT-RTX 1.6, use the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) first to verify install and run commands, then use this page for deployment planning.
Prerequisites
Complete the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) or equivalent steps so you have:
resnet50/model.onnx (or your own ONNX model)
resnet_engine.trt from an AOT build
A successful JIT inference run and resnet.cache from --runtimeCacheFile
Use the artifacts checklist at the end of the Quick Start if you are unsure all three outputs are present.
You should also have TensorRT-RTX installed. Refer to [Installation Guide Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installation-overview.html#installation-overview>) , [Installing TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html#installing>) , [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) , and the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) . To confirm the CLI is available before the walkthrough, run tensorrt_rtx --help (Linux) or tensorrt_rtx.exe --help (Windows). If that command fails, refer to [If a Command Fails](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-if-a-command-fails>) in the Quick Start Guide.
Deployment Pipeline
End-user deployment follows the same phases as the quick start, but your application owns when each phase runs:
For the offline AOT versus on-device JIT split and typical phase timing, refer to the [Architecture Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/architecture-overview.html#architecture-overview>) .
When to Run Each Phase
Phase
Typical timing in your app
Notes
AOT build ( --onnx / --saveEngine )
Install time, first launch, or a background update job
Does not require a GPU on the build machine. For most models, expect roughly 20–30 seconds; complex models can take up to about 60 seconds.
JIT compile (first --loadEngine inference)
First inference on the end-user GPU
Usually under 5 seconds for most models on first run. Kernel choices are specific to the user’s GPU.
Runtime cache ( --runtimeCacheFile )
After first successful JIT run
Persists compiled kernels so later launches skip JIT. Ship or regenerate alongside the engine when GPUs or TensorRT-RTX versions change.
Bundle Engines in Your Application
Treat the serialized engine and runtime cache as application data, not build artifacts you recompile on every launch.
Engine file : Build once per model version and TensorRT-RTX release you support. Store it under your installer payload or download it on first launch if the model updates frequently.
Runtime cache : Write after the first successful inference on the target GPU. On Windows, a common location is the user’s application data directory (for example, %LOCALAPPDATA% ). On Linux, use your application’s XDG data directory or an equivalent per-user store. The cache is GPU- and version-specific; regenerate it when the user upgrades their GPU or when you ship a new TensorRT-RTX runtime.
Tip
Run the AOT build during install or first launch so the user sees progress once, not on every application start. Warm up inference (or load an existing cache) before you measure or expose latency-sensitive UI.
Portability and Compute Capability
By default, an engine built with CPU-only AOT is portable across supported RTX GPUs on Linux and Windows with compute capability 7.5 and later (Turing and later). Engines built without specifying a compute capability target Ampere and later (8.6+) for optimal kernel selection on those architectures.
If you must support Turing explicitly, or you need to lock an engine to a narrower architecture list, set compute capabilities at build time. Refer to [CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) and the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) for CC-specific precision limits and build flags.
Use Your Own Model
The quick start uses ResNet-50 from the ONNX Model Zoo. For your own network:
Export to ONNX from your training framework. Refer to the [ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) .
Confirm every operator is supported. Refer to [Operators](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/api/operators.html#operators>) and the support matrix.
Build with the same tensorrt_rtx --onnx=... --saveEngine=... flow from the quick start, then apply the deployment notes on this page.
Move Beyond the CLI
The tensorrt_rtx tool is the fastest way to validate a model. Production applications usually embed the builder or runtime directly:
[Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>) : C++ and Python walkthroughs for build, deserialize, and enqueueV3 inference
[Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>) : torch.compile() with the TensorRT-RTX backend
[Working with Runtime Cache](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/work-with-runtime-cache.html#runtime-cache>) : Load, save, and version runtime-cache files programmatically
Next Steps
[Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) : Minimum three-command verify loop
[ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) : Export from PyTorch, TensorFlow, and other frameworks
[How TensorRT-RTX Works](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/how-trt-rtx-works.html#works>) : Object lifetimes, threading, and engine compatibility
[Best Practices](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/performance/best-practices.html#best-practices>) : Measure and optimize inference performance
