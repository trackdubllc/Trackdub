# Spleeter parity tests

Locks Trackdub’s **sherpa-onnx Spleeter 2stems** path (`csukuangfj/sherpa-onnx-spleeter-2stems`).

**Reference:** [k2-fsa/sherpa-onnx `scripts/spleeter/separate_onnx.py`](https://github.com/k2-fsa/sherpa-onnx/tree/master/scripts/spleeter) — not full Deezer TensorFlow Spleeter.

Production contracts live in `SpleeterModelConstants` (used by separator, engine, STFT processor, and these tests). Tests also pin **absolute** sherpa literals so a constants-only regression fails the suite.

| Contract | Value |
|---|---|
| Sample rate | 44100 Hz |
| STFT | n_fft 4096, hop 1024, keep 1024 bins |
| Time pad | `512 - (frames % 512)` when > 0 — **exact multiples still get +512** (sherpa) |
| Window | periodic Hann (locked via impulse STFT magnitudes) |
| Mask (production = sherpa) | `(stem^2 + eps/2) / (vocals^2 + accomp^2 + eps)`, `eps=1e-10` |
| HF bins | model-backed only `k < 1024`; inverse zeros the rest |
| ONNX I/O | `[2, num_splits, 512, 1024]`, input name from session metadata |
| Files | `vocals.onnx` + `accompaniment.onnx` (FP32, HF `main`) |

Tests do **not** require model downloads. Optional `generate_spleeter_reference.py` writes mono wav + sine/noise npz goldens.

Run:

```bash
dotnet test tests/Trackdub.Inference.Onnx.Tests --filter "FullyQualifiedName~Spleeter" -m:1
```
