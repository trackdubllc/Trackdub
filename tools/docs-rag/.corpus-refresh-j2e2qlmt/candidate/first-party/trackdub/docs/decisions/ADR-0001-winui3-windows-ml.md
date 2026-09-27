# ADR-0001: WinUI 3 + Windows ML

- Status: Draft
- Date: 2026-04-19

## Context

Trackdub is intentionally Windows-only, local-first, and hardware-aware. The app needs a desktop shell that can handle media preview, Windows packaging, composition-heavy UI, and long-running local AI workflows without pretending to be cross-platform.

The repository also has explicit boundary rules:

- inference code must not live in the WinUI project
- runtime/provider changes must remain visible to the user
- model execution has to be proven in a harness before the full editor depends on it

Current platform docs also support this direction:

- Windows App SDK exposes the Windows desktop app runtime and startup/deployment APIs used by WinUI 3 applications.
- ONNX Runtime's Windows guidance recommends the WinML path for Windows development, supports C# packages, and documents Windows execution-provider setup such as DirectML.

## Decision

Trackdub will use WinUI 3 on the Windows App SDK for the desktop shell and use a Windows-native ONNX inference stack for local model execution.

More specifically:

- `src/Trackdub.App` owns the WinUI 3 shell, navigation, resources, composition helpers, and user-facing state.
- `src/Trackdub.Inference` owns runtime planning, model manifests, provider selection, and inference interfaces.
- `src/Trackdub.Inference.Onnx` owns concrete ONNX model wrappers and Windows-specific runtime integration.
- The UI must never host model-wrapper code, session construction, or execution-provider policy.
- ONNX is the primary model interchange format for the first implementation slices.
- Provider selection is a runtime plan validated per model/provider pair, not a static "GPU on" toggle.
- **Superseded in part by [ADR-0002](ADR-0002-windows-ml-provider-strategy.md):** Windows ML is the primary Windows ONNX surface; catalog EPs are preferred where compatible; DirectML is legacy GPU fallback; CPU is terminal fallback. The bullets below remain historically accurate for the first WinUI slice but must not be read as current provider strategy.
- DirectML and CPU fallback were the initial baseline paths on Windows.
- TensorRT-RTX remains optional until it passes the benchmark harness on real target hardware (now integrated via the standalone ORT EP ABI plugin where available).
- If WinML-specific capabilities materially simplify Windows scenarios, they are consumed behind the `Trackdub.Inference.Onnx` boundary rather than exposed to the UI layer.

## Consequences

Positive:

- The UI stack matches the product's Windows-only scope.
- The app can use Windows-native packaging, lifecycle, composition, and media primitives without cross-platform abstraction tax.
- Inference remains testable and replaceable because the shell only depends on application and inference abstractions.
- The benchmark harness can validate provider behavior before the product claims GPU readiness.

Negative:

- This deliberately gives up cross-platform UI portability.
- Windows App SDK packaging/runtime behavior becomes part of the deployment surface that must be tested.
- Some models will still fail on specific providers, so benchmark evidence is required before enabling fast paths by default.

## Alternatives considered

### WPF

Rejected because it is mature but less aligned with the intended modern Windows app stack, composition direction, and long-term Windows App SDK packaging story.

### Avalonia or .NET MAUI

Rejected for the first product slice because Trackdub is not trying to be cross-platform yet, and the extra abstraction cost does not reduce the main project risks.

### Put inference directly in the WinUI project

Rejected because it would violate the repo boundary rules, make runtime policy harder to test, and encourage UI-driven provider shortcuts.

## References

- [Windows App SDK API reference](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/)
- [ONNX Runtime install and Windows guidance](https://onnxruntime.ai/docs/install/)
- [ONNX Runtime C# getting started](https://onnxruntime.ai/docs/get-started/with-csharp.html)
- [ONNX Runtime on Windows](https://onnxruntime.ai/docs/get-started/with-windows.html)
