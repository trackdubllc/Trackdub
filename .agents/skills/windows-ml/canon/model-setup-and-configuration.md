# Model setup and configuration

Tier 1: Microsoft Learn only. Checked 2026-10-10. This reference does not encode Trackdub manifests, exclusions, cache paths or model-family decisions.

## Obtain the model

Windows ML uses ONNX models. Microsoft's documented choices are prepared WinML CLI models, existing ONNX models, conversion from another framework, fine-tuning then conversion, or training then conversion. Target the ORT version included in the chosen Windows ML release. [Models](https://learn.microsoft.com/windows/ai/new-windows-ml/models), [version mapping](https://learn.microsoft.com/windows/ai/new-windows-ml/onnx-versions).

WinML CLI supports export, analyze, optimize, quantize and compile stages or an orchestrated build workflow. Windows ML's EP distribution does not automatically optimize arbitrary models for each hardware target. [WinML CLI](https://learn.microsoft.com/windows/ai/new-windows-ml/winml-cli), [acceleration](https://learn.microsoft.com/windows/ai/new-windows-ml/accelerate-ai-models).

The Learn overview does not establish exact CLI flags, full export recipes, model-specific tensor layouts, calibration data or legal rights for an individual model. Do not invent these or derive them from repository decisions.

## Runtime and CPU inference

Choose language, architecture, Windows target and deployment mode using [deployment instructions](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app). Establish CPU inference before optional acceleration. [Getting started](https://learn.microsoft.com/windows/ai/new-windows-ml/get-started).

Inputs, preprocessing and inference code differ by model. The general Learn example is:

```csharp
using Microsoft.ML.OnnxRuntime;

using InferenceSession session = new(modelPath, sessionOptions);
```

The application supplies `modelPath` and `sessionOptions`. Session creation alone is not a complete inference example. [Run ONNX models](https://learn.microsoft.com/windows/ai/new-windows-ml/run-onnx-models).

## Configure acceleration

Prepare/register the EP, enumerate its devices, filter to the intended provider and hardware class, and append the selection with documented options. Begin with explicit selection; evaluate automatic policies afterwards. [Selection](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers).

The generic selection example includes placeholder options. Placeholder names are not real configuration keys. Leave a provider-specific option unspecified if Microsoft Learn does not define it.

Thread spinning is disabled by default in Windows ML's ORT. The documented opt-in keys are `session.intra_op.allow_spinning` and `session.inter_op.allow_spinning`, set to `"1"`. Microsoft recommends testing both behaviors for performance and battery life. [Thread spinning](https://learn.microsoft.com/windows/ai/new-windows-ml/run-onnx-models).

## Generative inference

For LLMs and generative speech-to-text, Microsoft documents `Microsoft.ML.OnnxRuntimeGenAI.WinML` with Windows ML. GenAI supplies generation, preprocessing, token/logit processing, search/sampling and KV-cache management. The library remains a version-sensitive 0.x preview. [GenAI integration](https://learn.microsoft.com/windows/ai/new-windows-ml/run-genai-onnx-models).

That Learn page delegates model lists, exact configuration schemas and API reference to external ORT documentation. Under this source boundary, do not fabricate `genai_config.json` values, dimensions, token IDs, tokenizer layouts, provider options or supported models. State the gap.

Do not import application provider exclusions into Microsoft's platform capabilities. Conversely, documented GenAI support does not verify a particular export on a particular EP.

## Compile and cache

Microsoft distinguishes session graph optimization from hardware EP compilation. EP compilation can be expensive; EPContext serializes prepared artifacts for reuse. Compile-on-first-run is recommended for broad distribution; ahead-of-time distribution suits controlled hardware/driver fleets and still needs target validation/fallback. [Compilation](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation).

Microsoft's C# compile pattern uses `OrtModelCompilationOptions` with session options, input/output model paths and `CompileModel()`. Consult the full [run-models example](https://learn.microsoft.com/windows/ai/new-windows-ml/run-onnx-models) before adapting it.

| Compatibility result | Microsoft-documented meaning/action |
| --- | --- |
| `EP_SUPPORTED_OPTIMAL` | Supported and optimal; Microsoft samples reuse it |
| `EP_SUPPORTED_PREFER_RECOMPILATION` | Supported, but recompilation recommended; an optimal-only policy recompiles |
| `EP_UNSUPPORTED` | Do not load on these devices; recompile or use original |
| `EP_NOT_APPLICABLE` | No compatibility determination; not proof of validity |

Reusing a model that reports `EP_SUPPORTED_PREFER_RECOMPILATION` is allowed: it runs. Accepting only `EP_SUPPORTED_OPTIMAL` is the conservative policy Microsoft's samples choose, not a platform requirement. Pick the policy deliberately.

Microsoft's C# check for that conservative policy is shown below. The device list must be non-empty and come from one EP, the same one used to configure the session.

```csharp
static bool IsCompiledModelOptimal(OrtEnv env, string compiledPath, string epName, IReadOnlyList<OrtEpDevice> devices)
{
    string info = env.GetCompatibilityInfoFromModel(compiledPath, epName);
    return !string.IsNullOrWhiteSpace(info)
        && env.GetModelCompatibilityForEpDevices(devices, info) == OrtCompiledModelCompatibility.EP_SUPPORTED_OPTIMAL;
}
```

Extract opaque compatibility metadata using `GetCompatibilityInfoFromModel` and pass it with a nonempty same-EP device group to `GetModelCompatibilityForEpDevices`. Do not parse opaque metadata. Include source identity separately: EP validation does not establish the source model. Missing metadata or evaluation errors require the documented cache-miss/original-model behavior. [Compatibility policy](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation).

## Restrictions and unresolved details

See [provider restrictions](execution-providers.md). Hardware/driver, GenAI and WebGPU limitations do not form a complete operator/precision/shape matrix.

If Microsoft Learn does not establish required files, checksum/revision schema, input limits, quantization recipe, licenses, settings or provider compatibility for a specific model, label it unresolved. Do not fill gaps from ADRs, code decisions or outside sources.

The separate Runtime API has experimental restrictions and currently no C# support. Do not present its pipeline/tensor/GGUF APIs as the ordinary C# ONNX path. [Runtime API](https://learn.microsoft.com/windows/ai/new-windows-ml/runtime/overview).
