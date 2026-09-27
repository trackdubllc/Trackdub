Source: https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/refitting-engines.html

Refitting Engines
Refit updates constant weights in a serialized engine without a full rebuild. Weightless engines strip weights at build time and refit them on first launch (from custom buffers or ONNX). Enable kREFIT at build time, then use IRefitter or IParserRefitter at runtime.
Table 5 Refit-related deployment patterns Pattern
When to use
Related topics
Standard refittable engine ( kREFIT )
Update weights in place after deployment without re-running the full builder
[CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) : weightless workflow uses kREFIT with kSTRIP_PLAN
Weightless engine ( kSTRIP_PLAN + kREFIT )
Minimize installer size; ship a small engine and refit weights on first run
[CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) : Refitting a Weightless Engine
ONNX-driven refit ( IParserRefitter )
Weight-stripped engine built from ONNX; refit weights from the same ONNX file
[CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) : Refitting a Weightless Engine Directly with ONNX Models
Build flags, refit workflows, serialization after refit, and IParserRefitter examples are documented in [CPU-Only AOT and TensorRT-RTX Engines](<https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/inference-library/cpu-engines.html#cpu-engines>) .
