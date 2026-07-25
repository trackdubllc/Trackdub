# Olive recipe pilot

Trackdub can run [Olive](https://microsoft.github.io/Olive/) **recipe configs** for a small pilot set of bundled models instead of always using `olive auto-opt`.

## Scope

- **Automatic (Model Manager):** manifest `recipe_bindings` on pilot families `whisper-genai`, `whisper-onnx`, and `phi-genai`.
- **Fallback:** `auto-opt`, or bundled GenAI folder optimization when no recipe matches.
- **Developer override (ModelLab only):** `--olive-recipe-config <path>` runs `olive run --run-config <path>`.

End users still choose **execution provider** and **precision** only; there is no recipe picker in Model Manager.

## Recipes root

Recipe JSON files are **not vendored** in the repo. Set:

```powershell
$env:TRACKDUB_OLIVE_RECIPES_ROOT = "D:\Dev\olive-recipes"
```

Paths in `bundled-models.manifest.json` are relative to that root (for example `openai-whisper-tiny/cpu/whisper-tiny_cpu_int8.json`).

If the variable is unset or the config file is missing, optimization falls back to `auto-opt` and logs the reason in the optimization log.

## Manifest bindings

Optional `optimization.olive.recipe_bindings` entries:

| Field | Meaning |
|-------|---------|
| `provider` | `cpu`, `dml`, `cuda`, … (omit for any provider) |
| `precision` | `fp32`, `fp16`, `int8`, `int4`, … (omit for any precision) |
| `config_relative_path` | Path under `TRACKDUB_OLIVE_RECIPES_ROOT` |

Pilot models in the bundled manifest include Whisper GenAI (`openai/whisper-tiny`, `openai/whisper-base`), ONNX Whisper (`onnx-community/whisper-tiny`), and Phi GenAI (`microsoft/Phi-3.5-mini-instruct-onnx`).

## ModelLab override

```powershell
dotnet run --project src/Trackdub.Tools -- model-lab --olive-recipe-config "D:\Dev\olive-recipes\microsoft-Phi-3.5-mini-instruct\aitk\phi3_5_dml_config.json" ...
```

Unsupported in Model Manager UI; invalid override paths fail the run with an explicit error.

## Progress

Olive stdout lines matching `Step N/M` are also emitted as `[progress] Step N/M` for structured UI parsing; raw lines are still logged.

## Multi-component GenAI

When no recipe is selected and the model has multiple ONNX components in GenAI builder mode, optimization uses **shared bundled folder** `auto-opt` (`UseSharedComponentCache`) instead of per-component loops.
