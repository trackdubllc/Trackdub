Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/torch-trt-rtx.html

Using TensorRT-RTX via PyTorch
This walkthrough demonstrates how to accelerate PyTorch inference workloads using TensorRT-RTX. [Torch-TensorRT](<https://docs.pytorch.org/TensorRT/index.html>) is the project that introduces TensorRT and TensorRT-RTX as compilation backends for torch.compile() , optimizing the intermediate graphs captured by PyTorch Dynamo.
Installing Torch-TensorRT-RTX
Install the torch-tensorrt-rtx and tensorrt-rtx Python packages with pip :
python -m pip install torch-tensorrt-rtx
Install in a clean virtual environment when possible. The torch-tensorrt-rtx package constrains the compatible tensorrt-rtx versions, so pip might replace an existing TensorRT-RTX installation. Confirm
the resolved environment before testing:
python -m pip check
python -c "import torch_tensorrt, tensorrt_rtx; print(torch_tensorrt.__version__, tensorrt_rtx.__version__)"
For additional installation options and platform requirements, refer to the [Torch-TensorRT-RTX installation documentation](<https://docs.pytorch.org/TensorRT/getting_started/tensorrt_rtx.html>) .
Note
PyTorch’s TensorRT-RTX backend is in an experimental phase in this release. For the latest support status and known limitations, refer to the [upstream Torch-TensorRT-RTX documentation](<https://docs.pytorch.org/TensorRT/getting_started/tensorrt_rtx.html>) .
Compiling a PyTorch Model with TensorRT-RTX
To use TensorRT-RTX in PyTorch, compile your model with the backend set to "tensorrt" :
import torch import torch_tensorrt class MyModel ( torch . nn . Module ): def __init__ ( self ): super () . __init__ () self . layers = torch . nn . Sequential ( torch . nn . Conv2d ( 3 , 8 , kernel_size = 3 , padding = 1 ), torch . nn . ReLU (), torch . nn . Conv2d ( 8 , 8 , kernel_size = 3 , padding = 1 ), torch . nn . ReLU (), torch . nn . Conv2d ( 8 , 8 , kernel_size = 3 , padding = 1 ), torch . nn . ReLU (), ) def forward ( self , x ): return self . layers ( x ) model = MyModel () . eval () . cuda () x = torch . randn (( 1 , 3 , 224 , 224 )) . cuda () optimized_model = torch . compile ( model , backend = "tensorrt" ) optimized_model ( x ) # Compiled on first run output = optimized_model ( x ) # Subsequent runs are fast torch . cuda . synchronize () print ( output . shape )
Listing 6 Expected output
torch.Size([1, 8, 224, 224])
Note
Two naming conventions catch most users by surprise:
Import the package as torch_tensorrt (not torch_tensorrt_rtx ).
Pass the backend name as "tensorrt" (not "tensorrt-rtx" ).
For the full Torch-TensorRT API, supported precisions, and advanced compilation options, refer to the [Torch-TensorRT documentation](<https://docs.pytorch.org/TensorRT/index.html>) .
Next Steps
After your PyTorch model is running with TensorRT-RTX:
[Using the Native Runtime API](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/runtime-api.html#runtime-api>) : C++ and Python API walkthroughs for direct integration
[Quick Start Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/quick-start-guide.html#quick-start-guide>) : Verify ONNX build and inference with the CLI; [Build Your First Engine](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/getting-started/build-your-first-engine.html#build-your-first-engine>) : Deployment and portability after Quick Start
[ONNX Conversion Guide](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/installing-tensorrt-rtx/onnx-conversion.html#onnx-conversion>) : Export PyTorch models to ONNX for use with the native TensorRT-RTX API
[Architecture Overview](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/architecture/architecture-overview.html#architecture-overview>) : Compare ONNX, native API, and PyTorch entry points
