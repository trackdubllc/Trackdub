Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/installing.html

Installing TensorRT-RTX
TensorRT-RTX can be installed from an SDK zip file on Windows, a tarball on Linux, or via PyPI for Python workflows. Before proceeding, ensure you have met all [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) .
Windows SDK Installation [Download](<https://developer.nvidia.com/tensorrt-rtx>) the Windows zip package that matches your CUDA Toolkit version and CPU architecture.
Decompress the Windows zip package to extract the following:
Core library and ONNX parser DLLs and import libraries
Development headers
Source code samples
Documentation
Python bindings
The tensorrt_rtx executable for building engines and running inference from the command line
Licensing information and open-source acknowledgments
Note
The DLLs are signed and verified with the signtool utility. Add the package lib and bin directories to your PATH so Windows can find the DLLs and tensorrt_rtx.exe :
$trtRoot = "C:\path\to\TensorRT-RTX-1.6.0.0" # Replace with your extract path $env:PATH = "$trtRoot\lib;$trtRoot\bin;$env:PATH"
Confirm the CLI resolves:
tensorrt_rtx . exe - -help
If this fails with a “not recognized” or missing-DLL error, the lib and bin directories are not on your PATH . Refer to [If a Command Fails](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-if-a-command-fails>) .
Optionally, install the Python bindings.
$version = "1.6.0.0" # Replace with newest version $arch = "amd64" # Replace with your architecture $pyversion = "311" # For Python 3.11. Replace with your # Python version for other versions. $wheel = Join-Path "TensorRT-RTX-$version" "tensorrt_rtx-$version-cp$pyversion-none-win_$arch.whl" python3 -m pip install $wheel
Linux Tarball Installation TensorRT-RTX can be installed from a tarball package on Linux. For supported distributions and CUDA requirements, refer to the [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) page.
[Download](<https://developer.nvidia.com/tensorrt-rtx>) the tarball that matches your operating system version and CPU architecture.
Unzip the tarball, optionally adding the path of the executable to your PATH variable and the path of the library to your LD_LIBRARY_PATH variable.
version = "1.6.0.0" # Replace with newest version arch = "x86_64" # Replace with your architecture cuda = "12.9" # Replace with your CUDA version tarfile = "TensorRT-RTX- ${ version } .Linux. ${ arch } -gnu- ${ cuda } .tar.gz" tar -xzf $tarfile export PATH = $PATH : $PWD /TensorRT-RTX- ${ version } /bin export LD_LIBRARY_PATH = $LD_LIBRARY_PATH : $PWD /TensorRT-RTX- ${ version } /lib
Confirm the CLI resolves:
tensorrt_rtx --help
If this fails with command not found or a missing shared-library error, PATH or LD_LIBRARY_PATH is not set for the current shell. Refer to [If a Command Fails](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-if-a-command-fails>) .
Optionally, install the Python bindings.
pyversion = "311" # Assuming Python 3.11, else replace with your # Python version wheel = "tensorrt_rtx- ${ version } -cp ${ pyversion } -none-linux_ ${ arch } .whl" python3 -m pip install TensorRT-RTX- ${ version } /python/ ${ wheel }
PyPI Installation (Python) To use TensorRT-RTX from Python, you can install the tensorrt-rtx package directly from PyPI. This provides the Python bindings and the TensorRT-RTX shared library.
On Linux images that ship an older system pip (for example Ubuntu 22.04 with pip 22.x), upgrade pip first. Older pip versions can reject the TensorRT-RTX source distribution with an inconsistent package-metadata error.
python3 -m pip install --upgrade pip
python3 -m pip install tensorrt-rtx
Confirm the import:
python3 -c "import tensorrt_rtx; print(tensorrt_rtx.__version__)"
Note
If you installed TensorRT-RTX from the Windows SDK or Linux tarball, you do not need to install the PyPI package separately; the local wheel included in those distributions provides the same Python bindings.
The PyPI package does not include the tensorrt_rtx CLI. For the [Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) walkthrough, use the Windows SDK zip or Linux tarball tab instead.
Python inference examples that use pycuda also require a CUDA Toolkit install so pycuda can build or find CUDA headers. Refer to [Prerequisites](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/prerequisites.html#prerequisites>) .
Next Steps
Windows SDK or Linux tarball
After installation, confirm the CLI responds ( tensorrt_rtx --help on Linux or tensorrt_rtx.exe --help on Windows), then:
[Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) : Verify install and run the minimum ONNX build and inference loop. If --help fails, refer to [If a Command Fails](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-if-a-command-fails>) in that guide.
[Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) : Bundle engines, runtime caches, and portability for production apps
PyPI (Python)
After pip install , confirm python3 -c "import tensorrt_rtx" succeeds, then:
[Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>) : Build and run inference from Python
[Using TensorRT-RTX via PyTorch](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html#torch-tensorrt-rtx>) : Accelerate models with torch.compile()
All install methods
[ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) : Export your own model to ONNX from PyTorch, TensorFlow, or Hugging Face
[Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>) : C++ or Python integration beyond the CLI quick start
