Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html

Prerequisites
Before installing TensorRT-RTX, ensure your system meets the following requirements.
Table 1 Quick prerequisites checklist Requirement
Details
GPU
NVIDIA RTX GPU, Turing (compute capability 7.5) or later
CUDA Toolkit
CUDA 12.9 Update 1 or CUDA 13.4 installed (see Required Software)
Drivers
NVIDIA GPU driver that meets the minimum for your CUDA Toolkit release, as listed in the [CUDA Toolkit driver requirements](<https://docs.nvidia.com/cuda/cuda-toolkit-release-notes/index.html#id5>)
Python (optional)
3.10–3.14 recommended for wheels; 3.8–3.9 supported
Verify the GPU, driver, and CUDA Toolkit before you install:
nvidia-smi
nvcc --version
nvidia-smi reports the GPU model and installed driver version. nvcc --version confirms the CUDA Toolkit on your PATH . Compare the GPU against the hardware table below (or the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) ) to confirm compute capability, and compare the driver version against the [CUDA Toolkit driver requirements](<https://docs.nvidia.com/cuda/cuda-toolkit-release-notes/index.html#id5>) table for your toolkit release. If the driver is older than the minimum for your toolkit, update the driver before you continue.
Required Hardware
Table 2 Supported NVIDIA RTX hardware GPU family
Architecture
Compute capability
RTX 2000-series
Turing
7.5
RTX 3000-series
Ampere
8.6
RTX 4000-series
Ada Lovelace
8.9
RTX 5000-series
Blackwell
12.0
DGX Spark series
Blackwell
12.1
For a complete list of supported GPUs and precision modes, refer to the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) .
Required Software
CUDA Toolkit
NVIDIA [CUDA 12.9 Update 1](<https://developer.nvidia.com/cuda-toolkit-archive>) or [CUDA 13.4](<https://developer.nvidia.com/cuda-toolkit-archive>) Toolkit
Note
Separate TensorRT-RTX packages are available for CUDA 12.9 Update 1 and CUDA 13.4.
Which CUDA package should I download?
Match the package to the CUDA Toolkit already on your system, which nvcc --version reports:
nvcc reports 12.x → download the CUDA 12.9 package.
nvcc reports 13.x → download the CUDA 13.4 package.
No CUDA Toolkit installed, or you are starting fresh → install CUDA 13.4 and use the CUDA 13.4 package.
Do not mix packages across major versions: a CUDA 13 build of TensorRT-RTX does not load against a CUDA 12 toolkit installation.
Operating System
For required software and supported platforms, refer to the [Support Matrix](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/support-matrix.html#support-matrix>) .
NVIDIA Developer Program
You must be a member of the NVIDIA Developer Program to download TensorRT-RTX.
Go to the [TensorRT-RTX download page](<https://developer.nvidia.com/tensorrt-rtx>) .
Click GET STARTED , then click Download Now .
Select the version of TensorRT-RTX that you are interested in.
Select the checkbox to agree to the license terms.
Click the package you want to install. Your download begins.
Optional Dependencies
Optional Dependencies Python Bindings
Python 3.8–3.14 supported
- Recommended : Python 3.10–3.14 (fully tested and optimized)
Note
The Windows on ARM package only supports Python 3.11-3.14.
Legacy support : Python 3.8–3.9 (wheels available, but may have limited testing)
pip package manager for installing the TensorRT-RTX Python wheel
PyCUDA
When using TensorRT-RTX with the PyCUDA library in Python, use import pycuda.autoprimaryctx instead of import pycuda.autoinit to avoid device conflicts.
Next Steps
After verifying prerequisites, proceed to [Installing TensorRT-RTX](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html#installing>) for step-by-step instructions on setting up TensorRT-RTX using:
Windows SDK zip : Extract, add DLLs to your PATH , and optionally install Python bindings
Linux tarball : Extract, set LD_LIBRARY_PATH , and optionally install the Python wheel
PyPI (Python) : pip install tensorrt-rtx for Python bindings and the runtime library. For the tensorrt_rtx CLI, C++ headers, and samples, use the Windows SDK zip or Linux tarball instead.
After SDK or tarball install: Confirm tensorrt_rtx --help (or tensorrt_rtx.exe --help on Windows), then run the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) . If --help fails, refer to [If a Command Fails](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-if-a-command-fails>) in that guide.
After PyPI install: Confirm python3 -c "import tensorrt_rtx" succeeds, then use [Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>) or [Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>) . For the CLI-based Quick Start, install the Windows SDK zip or Linux tarball instead.
