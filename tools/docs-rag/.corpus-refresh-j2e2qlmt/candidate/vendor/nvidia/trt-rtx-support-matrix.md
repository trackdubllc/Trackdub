Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html

Support Matrix
This support matrix provides filterable access to TensorRT-RTX compatibility information across all releases from 1.0 through 1.6. Use the two explorers below to find information about system requirements and hardware capabilities.
Software and System Requirements
Use this when you need to know what to install or what versions are required
Engine Portability
By default, TensorRT-RTX engines are portable across devices with compute capabilities 8.6, 8.9, 12.0, and 12.1, provided that the model precision is supported on the target architecture.
Note: Turing (7.5) compatibility is not included by default because including it may impact model performance. We encourage developers to build optimized, device-specific engines for Turing using appropriate API flags and deploy two engines: one for Turing and one for other architectures.
Find out what you need to install and run TensorRT-RTX. First select your TensorRT-RTX version, then optionally select a platform to view CUDA versions, Python requirements, and compiler versions. If no platform is selected, you'll see all supported platforms for the chosen TensorRT-RTX version.
Step 1: Select TensorRT-RTX Version -- Select a version -- 1.6 (Latest) 1.5 1.4 1.3 1.2 1.1 1.0
Step 2: Select Platform (Optional) -- Select a platform --
Select a TensorRT-RTX version above to view compatibility information. Optionally select a platform to filter results.
GPU Architecture and Precision Support
Use this when you need to know hardware capabilities and precision support
Find out which GPU architectures and precision modes are supported. Filter by GPU architecture (compute capability) or precision mode to see which hardware supports which precision types. TensorRT-RTX supports NVIDIA hardware with compute capability SM 7.5 or higher.
GPU Architecture -- Select compute capability -- All GPU Architectures 12.1 (NVIDIA Blackwell) 12.0 (NVIDIA Blackwell) 8.9 (NVIDIA Ada Lovelace) 8.6 (NVIDIA Ampere) 7.5 (NVIDIA Turing)
Precision Mode -- Select precision mode -- FP32 FP16 BF16 INT8 WoQ GEMM INT4 WoQ GEMM FP8 FP4 GEMM
Select filters above to display GPU architecture and precision support.
Precision limitations
FP8 convolutions and matrix multiplications require 32-bit/4-byte alignment. As such, certain problem sizes may suffer poor performance when using FP8. For example, depthwise convolutions will be padded on the C dimension to meet alignment requirements.
FP8 deconvolutions are not supported.
Footnotes
[#f1] TensorRT-RTX on Turing (compute capability 7.5) does not support FP32 GEMMs and 3D convolutions in this release.
[#f2] Separate TensorRT-RTX packages are provided for CUDA 12.9 Update 1 and CUDA 13.4 on x86-64 platforms. Linux SBSA and Windows on ARM packages are available only for CUDA 13.4.
[#f3] Linux SBSA and Windows on ARM were validated only with compute capability 12.1.
