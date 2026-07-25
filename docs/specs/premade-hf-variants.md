# Premade HF pack variants

Publisher-hosted ONNX variants let low-spec machines skip local Olive. Trackdub pulls the variant declared in each starter pack `runtime_defaults` row during **pack download**, not only at apply/runtime.

## Product behavior

1. Hardware profiler resolves `cpu_safe`, `balanced_gpu`, or `turbo_gpu`.
2. `StarterPackDownloadService` maps each spine model to `runtime_defaults[profile].variant`.
3. `ModelDownloadOrchestrator.DownloadAsync(modelId, variantAlias)` pulls base `download_files` plus that variant's files from Hugging Face (or `download_file_sources`).

Apply still writes the same variant overrides to settings; download and apply stay aligned.

## When to publish your own HF repos

Use a **Babelworks** or **tonythethompson** repo when:

- Upstream has no portable quant for the EP you want (common for custom Olive outputs).
- You want a single curated DirectML int4 bundle per model for balanced tier.
- You need smaller CPU packages than upstream ships.

Do **not** premake and upload:

- TensorRT / TRT-RTX engines (GPU-specific).
- Per-machine Olive outputs unless you version by SKU (high maintenance).

## Repo naming convention

```
tonythethompson/trackdub-{short-name}-{variant}
```

Examples (published):

- [tonythethompson/trackdub-silero-vad-int8](https://huggingface.co/tonythethompson/trackdub-silero-vad-int8)
- [tonythethompson/trackdub-silero-vad-fp16](https://huggingface.co/tonythethompson/trackdub-silero-vad-fp16)
- [tonythethompson/trackdub-kokoro-q8f16](https://huggingface.co/tonythethompson/trackdub-kokoro-q8f16)
- [tonythethompson/trackdub-kokoro-fp16](https://huggingface.co/tonythethompson/trackdub-kokoro-fp16)
- [tonythethompson/trackdub-phi-4-mini-cpu-int4](https://huggingface.co/tonythethompson/trackdub-phi-4-mini-cpu-int4)
- [tonythethompson/trackdub-phi-4-mini-gpu-int4](https://huggingface.co/tonythethompson/trackdub-phi-4-mini-gpu-int4)

Keep the same relative paths as the upstream manifest variant (`onnx/model_int8.onnx`, etc.) so manifest `download_file_sources` overrides stay minimal.

## Publishing workflow

1. Run Olive (or copy upstream quant) into a clean output folder matching manifest paths.
2. Verify commercial license and compute SHA-256 for each file.
3. Upload with `tools/models/Publish-TrackdubPackVariant.ps1`.
4. Add or update `bundled-models.manifest.json`:
   - Either add `download_file_sources` for specific paths, or
   - Add a new `model_id` pointing at your repo if it is a full mirror.
   - Record SHA-256 in `download_file_hashes` for each mirrored path (see `tools/ci/hashes-*-premade-variants.json` for Silero, Kokoro, Phi).
5. Run manifest validation tests and `dotnet test tests/Trackdub.Composition.Tests --filter StarterPack`.

## Manual download smoke

After publishing mirrors and updating `download_file_sources`, verify HF resolve URLs:

```powershell
# Full: every mirror URL in manifest (includes large Phi ONNX weights)
.\tools\models\Smoke-PremadePackVariantDownloads.ps1

# Quick: one reachability probe per mirror repo (HEAD or 1-byte range; no full ONNX download)
.\tools\models\Smoke-PremadePackVariantDownloads.ps1 -Quick

# Or directly:
python tools/ci/smoke-premade-pack-variants.py --quick
python tools/ci/verify-manifest-hashes.py --model-id microsoft/Phi-4-mini-instruct-onnx
```

## Bundled CI scope (same branch)

This branch also restores self-hosted OpenCode/Cursor review workflows (`.github/workflows/opencode-review.yml`, `cursor-code-review.yml`). That automation is repo-wide PR infra, not part of premade variant download behavior. See [docs/GITHUB_ACTIONS.md](../GITHUB_ACTIONS.md).

## Manifest gates

- `commercial_use_verified` must stay true for shipping models.
- Update `THIRD_PARTY_NOTICES.md` when attribution is required.
- Never mark Olive-local variants as HF-redistributable unless files are actually on HF.
