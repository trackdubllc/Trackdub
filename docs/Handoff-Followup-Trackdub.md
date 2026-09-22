# Handoff: real SkipLayerNormalization / BiasGelu decomposition for TRT-RTX Olive recipes

Repo: trackdubllc/Trackdub. Follow-up to PR #247 (branch `Olive`, last commit c7222ad).

## Goal

Make the SortFormer and Nemotron TRT-RTX Olive recipes actually work, then enable trt-rtx for those two models. The recipes are meant to rewrite

- `com.microsoft::SkipLayerNormalization` -> `Add` + `LayerNormalization`
- `com.microsoft::BiasGelu` -> `Add` + `Gelu`

before fp16/mxfp8 conversion, so TensorRT-RTX can parse the graph instead of falling back to CPU (the smoke gate fires `preFlightFailed` when requested provider `tensorrt-rtx` != effective provider `cpu`).

## State after PR #247 (verified)

- PR #247 was scoped down: manifest, `OliveRecipeResolver.PilotEngineFamilies`, and `ModelManifestTests.cs` were restored to main, so trt-rtx is NOT advertised or routed for sortformer/nemotron-asr. Do not re-enable it until step 5.
- Still on the branch as unvalidated tooling: recipe JSONs, READMEs (with a "Known issue" note), `Validate-SortFormerTrtRtx.ps1`, `Validate-NemotronAsrTrtRtx.ps1`, `Bootstrap-TrtRtxOliveVenv.ps1`, `Flip-TrtRtxAsrDiarization.ps1`.
- The recipes use `"type": "GraphSurgeries"` with surgeons `ReplaceNodePatternByNode` and `RemoveIdentityAndCastNodes`. Neither exists in olive-ai's Surgeon registry (checked in installed olive-ai 0.13.0, `olive/passes/onnx/graph_surgeries.py`; registry keys are lowercased class names; that file contains every Surgeon subclass). These passes fail at run time. No existing Olive surgeon does this rewrite.
- The original `OnnxBlockWiseRMSN` pass was also wrong: it is an RMSNorm quantization pass and does not touch SkipLayerNormalization.
- Correct Olive pass type name is `GraphSurgeries`; surgery entries are keyed `"surgeon"` (not `surgeon_type`). Working reference: `resources/olive-recipes/google-bert-bert-base-multilingual-cased/aitk/bert-base-multilingual-cased_trtrtx.json`.
- The claim that the models actually contain these ops comes from the original PR README and has NOT been verified against the real graphs.

## Files involved

- `resources/olive-recipes/cgus-diar_streaming_sortformer_4spk-v2.1-onnx/NvTensorRtRtx/encoder_trtrtx_{fp16,mxfp8}.json`, `README.md`
- `resources/olive-recipes/nemotron-3.5-asr-streaming-0.6b-onnx/NvTensorRtRtx/{encoder,decoder_joint}_trtrtx_{fp16,mxfp8}.json`, `README.md`
- `tools/olive/Validate-SortFormerTrtRtx.ps1`, `Validate-NemotronAsrTrtRtx.ps1`, `Bootstrap-TrtRtxOliveVenv.ps1`, `Flip-TrtRtxAsrDiarization.ps1`
- Model inputs: `models/sortformer/cgus-diar_streaming_sortformer_4spk-v2.1-onnx/onnx/model.onnx`, `models/tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx/{encoder,decoder_joint}.onnx`

## Steps

0. Gate. Before any code: confirm GPU + TRT-RTX (NvTensorRTRTXExecutionProvider) and the model files are available. If not, say so and stop. Do not ship an unverified rewrite.

1. Inspect the real ONNX files (onnx Python). List op types; count SkipLayerNormalization / BiasGelu nodes; record each node's `epsilon`, whether the optional 5th `bias` input is present, which outputs are consumed, and the model's opset imports. Report findings before writing any rewrite. If the ops are not present, stop and report; the premise of the recipes is wrong. Also check whether TRT-RTX already handles the `com.microsoft` ops (if so, the rewrite may be unnecessary and the CPU fallback has another cause).

2. Write the rewrite as a standalone script (onnx or onnxscript.rewriter) under `tools/olive/`, run as a pre-step before `olive run`. Do not guess Olive's custom-pass plugin API; if a custom Olive pass is wanted, verify the registration mechanism against installed source and an existing working example first.
   - SkipLayerNormalization: fold the optional bias into the Add; carry `epsilon` onto LayerNormalization (ORT contrib default is 1e-12, ONNX LayerNormalization default is 1e-5); wire any consumed extra outputs: `mean`, `inv_std_var`, and the fourth output `input_skip_bias_sum`. Match the 4-input and 5-input forms.
   - BiasGelu: Add + Gelu. Gelu is opset 20 and LayerNormalization is opset 17; raise the model's opset import if required, or decompose Gelu via Erf.

3. Remove the fictional surgeons from all six recipe JSONs and the "Known issue" note from both READMEs once the real step exists. Update `Bootstrap-TrtRtxOliveVenv.ps1` to install any new dependency (onnx / onnxscript). Have the validators invoke the pre-step before `olive run`.

4. Verification (most of the work):
   - Assert no `com.microsoft::SkipLayerNormalization` / `BiasGelu` nodes remain.
   - Run original and rewritten graphs on CPU with identical inputs; compare outputs within tolerance. Use realistic inputs, not only random.
   - Run the rewritten graph on NvTensorRTRTXExecutionProvider and confirm the effective provider is trt-rtx, not cpu.
   - Replace the validators' `pass = output file exists` logic with a check that performs the two verifications above; `Flip-TrtRtxAsrDiarization.ps1` trusts that flag.
   - Add a post-optimization check that no SkipLayerNormalization/BiasGelu nodes remain in the staged model.

5. Only after step 4 passes on hardware, run `Flip-TrtRtxAsrDiarization.ps1` and commit. Note: Flip does NOT add `sortformer` and `nemotron-asr` to `OliveRecipeResolver.PilotEngineFamilies`. Without that, the resolver returns AutoOpt and bypasses the bindings. Add that to Flip (or do it by hand) and add resolver coverage. Also restore the manifest tests: SortFormer trt-rtx binding assertion and nemotron encoder + decoder_joint binding assertions.

## Other open items to close in the follow-up

- Duplicate nemotron manifest bindings: encoder and decoder_joint bindings both match `trt-rtx` + `fp16`, and `OliveRecipeResolver.Resolve` takes `FirstOrDefault`, so decoder_joint is never selected. Fix with either a `component` key on `ModelOptimizationRecipeBinding` plus a resolver change, or one recipe per model that covers both components.
- Validator `pass` flag is file-existence only (Copilot review comments on both validators); Flip trusts it. Covered by step 4.
- Epsilon preservation and the optional 5-input bias variant (cubic comments on the sortformer/nemotron encoder recipes). Covered by step 2.
- Validators hardcode `$RepoRoot\models\...` and ignore `TRACKDUB_MODEL_CACHE` (all three validators, including Whisper). Decide whether to support the env var.
- Review asks left as out of scope in PR #247: extract shared bootstrap/recipe-resolve/staging helper across the three validator scripts; parameterized staging helper for encoder/decoder in the Nemotron validator.
- `.mcp.json` registers a third-party NVIDIA MCP server (`nvidia-cuda-docs`) for every agent that loads the workspace. cubic raised this as a governance concern; the owner should decide whether to keep it committed. Not a code bug.
- Optional: `SortFormerDiarizationEngineTests.cs` has no TRT-RTX smoke test; add one mirroring `WhisperOnnxTrtRtxValidationTests.cs` (`[Fact(Skip = "Pending TRT-RTX validation ...")]`) once validated.
- CodeFactor was failing on PR #247 the whole time; no details are exposed through `gh`. Check its dashboard.

## Rules

- Verify every Olive/onnxscript API against installed source before using it. A plausible-sounding API name was wrong twice on PR #247.
- Do not mark PR review threads resolved unless the behavior is actually verified.
- Follow repo instructions (CLAUDE.md): use Serena symbolic tools for C# code; commit only when asked; stage files by name.
- If hardware or models are unavailable, say so and stop.
