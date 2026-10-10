# NuGet package facts

Tier 2. Observed in package contents and nuspec metadata, not stated on Microsoft Learn. Each fact holds for the package version named. Re-inspect the package after any version change, because names, layouts and dependencies can move.

Checked 2026-10-10 in the local NuGet cache.

## Microsoft.Windows.AI.MachineLearning 2.4.89

- Native: `runtimes/win-x64/native/onnxruntime.dll` (product version `1.27.20260711.1.7b7c0e2`), `DirectML.dll`, `Microsoft.Windows.AI.MachineLearning.dll`.
- Managed: `lib/net8.0-windows10.0.17763.0/Microsoft.ML.OnnxRuntime.dll` (assembly version `0.0.0.0`) and `Microsoft.Windows.AI.MachineLearning.Projection.dll`. The package therefore ships its own managed ORT API, built to match its native ORT.
- **Consequence (inferred, standard .NET SDK conflict resolution).** A project that also references a stock `Microsoft.ML.OnnxRuntime*` package gets two copies of `Microsoft.ML.OnnxRuntime.dll`. The build normally keeps the one with the higher assembly version, which is the stock one. Confirm which copy is in the output folder.
- Runtime dependency: `System.Numerics.Tensors`.

## Microsoft.ML.OnnxRuntimeGenAI.WinML 0.17.1

- Native (win-x64): `onnxruntime-genai.dll` and `onnxruntime-genai-cuda.dll`. It carries no `onnxruntime.dll`; it uses Windows ML's.
- Nuspec dependencies: `Microsoft.ML.OnnxRuntimeGenAI.Managed` `0.17.1`, and `Microsoft.Windows.AI.MachineLearning` `2.1.1` (a NuGet minimum, inclusive).
- **Consequence.** Without a direct reference to a newer Windows ML package, NuGet resolves the lowest version allowed, 2.1.1, which ships ORT 1.24.6 according to the Learn version table. Pin Windows ML explicitly. Read `requested`/`dependencies` in `packages.lock.json`; registry tools that report a "resolved latest" version can mislead.

## Microsoft.ML.OnnxRuntime.Gpu.Windows 1.30.0

- The `runtimes/` folder contains `win-x64` only. There is no `win-arm64` CUDA build.
