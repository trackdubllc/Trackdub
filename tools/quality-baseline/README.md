# Quality baselines before contrib-op decompose

Purpose: prove graph surgery (`decompose-microsoft-contrib-ops.py`, MHA decomposer) does **not** substantially change ASR/TTS quality.

## Protocol

1. **Before** any surgery, run this checklist and store outputs under
   `%LOCALAPPDATA%\Trackdub\quality-baselines\pre-decompose\`.
2. Decompose models in place (or produce sidecar copies).
3. **After**, re-run the same commands into `post-decompose\`.
4. Compare with `compare_transcripts.py` (WER/CER vs **pre-decompose golden**, and optionally vs ground-truth refs).
5. Accept if WER(pre, post) ≤ **0.02** and CER ≤ **0.01** for ASR (surgery is meant to be semantics-preserving).
   Absolute quality (vs human refs) should not regress by more than **+0.02 WER**.

## ASR fixtures

| id | media | models (decompose candidates) |
|----|-------|-------------------------------|
| short-en | `%LOCALAPPDATA%\Trackdub\benchmark-fixtures\baseline-v1\short.mp4` | `whisper-small`, `qwen3-asr-0.6b`, `qwen3-asr-1.7b` |
| clean-wav | `D:\Dev\Trackdub_Workspace\clip-clean-bed-dub\dub-clean.wav` | same |

## Capture (pre-decompose)

```powershell
$root = "$env:LOCALAPPDATA\Trackdub\quality-baselines\pre-decompose"
$fix  = "$env:LOCALAPPDATA\Trackdub\benchmark-fixtures\baseline-v1\short.mp4"
$sha  = "c4640c3f8062b4d928eeef25c52f845f4867c10f26aeb4f5d5ce6be1c295bd85"

foreach ($model in @("whisper-small","qwen3-asr-0.6b","qwen3-asr-1.7b")) {
  dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 --no-build -- `
    controlled $fix --output "$root\$model" --mode fresh-process --reuse-engine-cache `
    --language es --source-language en --model $model --stage asr --sha256 $sha
}
# Then copy each project's artifacts/transcript/raw-asr-*.json to
#   $root\short-en\$model.json
```

## TTS fixtures (chatterbox-turbo)

Fixed phrases (UTF-8, one line each) in `tts-phrases.txt`. Run TTS stage after a
tiny ASR project exists, or via DubBench. Save WAVs as `tts/{model}/{index}.wav`.
Compare pre/post with:

- duration delta < 2%
- RMS delta < 5%
- optional: ASR the TTS audio and WER(pre_tts_text, asr(wav))

## Models in scope for surgery

- `openai/whisper-*` encoder+decoder — SkipLayerNormalization, MultiHeadAttention
- `tonythethompson/qwen3-asr-{0.6b,1.7b}-onnx` encoder — BiasGelu, SkipLayerNormalization
- `ResembleAI/chatterbox-turbo-ONNX` — MultiHeadAttention, FastGelu

Record each file's SHA-256 in `pre-decompose/file-sha256.txt` so the after run
cannot accidentally compare different weights.
