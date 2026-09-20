# Spleeter parity tests

Locks Trackdub’s **sherpa-onnx Spleeter 2stems** path (`csukuangfj/sherpa-onnx-spleeter-2stems`).

**Reference:** [k2-fsa/sherpa-onnx `scripts/spleeter/separate_onnx.py`](https://github.com/k2-fsa/sherpa-onnx/tree/master/scripts/spleeter) — not full Deezer TensorFlow Spleeter.

Production contracts live in `SpleeterModelConstants` (used by separator, engine, STFT processor, and these tests):

| Contract | Value |
|---|---|
| Sample rate | 44100 Hz |
| STFT | n_fft 4096, hop 1024, keep 1024 bins |
| Time pad | `512 - (frames % 512)` when > 0 — **exact multiples still get +512** (sherpa) |
| Window | periodic Hann |
| Mask | production `ComputeSoftMasks`: \(v^2/(v^2+a^2+\epsilon)\), \(\epsilon=10^{-10}\) |
| Mask (sherpa) | \((stem^2+\epsilon/2)/(v^2+a^2+\epsilon)\) — tiny documented delta |
| HF bins | model-backed only \(k<1024\); inverse zeros the rest |
| ONNX I/O | `[2, num_splits, 512, 1024]`, input name from session metadata |
| Files | `vocals.onnx` + `accompaniment.onnx` (FP32, HF `main`) |

Tests do **not** require model downloads. Optional `generate_spleeter_reference.py` writes mono wav + npz goldens if you extend C# to load them.

Run:

```bash
dotnet test tests/Trackdub.Inference.Onnx.Tests --filter "FullyQualifiedName~Spleeter" -m:1
```
