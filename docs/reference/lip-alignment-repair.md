# Lip alignment and repair status

The canonical stage names are `lip-sync` for forced alignment and phoneme
stretching, and `lip-synthesis` for video repair. The desktop calls the latter
**Repair lips**; the pipeline CLI flag is `--repair-lips`.

## Headless selection

Full `run pipeline` calls omit both lip stages unless opted in. `--lip-sync`
selects alignment after TTS. `--repair-lips` selects the mix-producing Export,
then video repair, then a final Export when repair succeeds or partially
succeeds. Both flags select both lip stages. `--only` names stages directly and
cannot be combined with either flag. A flag is rejected if `--from-stage`
starts after its stage. `--only lip-synthesis` requires a pre-existing dubbed
mix and reports `LipSynthesisNoDubbedMix` when it is absent.

For a pipeline run, use `--model lip-synthesis:latentsync-1.6` to pin the
repair model. `run stage --stage lip-synthesis --model latentsync-1.6` takes a
bare alias. A successful standalone repair refreshes the final Export and
reports its outcome and artifact paths.

## Current model boundary

The published `latentsync` alias still resolves to 1.6. The shipped ONNX
repair engine remains unavailable: its renderer still operates on individual
frames and writes over the whole frame, whereas the upstream 1.5 U-Net takes a
13-channel, five-dimensional temporal input. The guard preserves original
video and prevents an unvalidated render from being reported as repaired.
Neither a registered provider nor downloaded weights means repair succeeded.
The disabled engine's input preparation now parses RIFF chunks rather than
assuming a 44-byte WAV header, downmixes common PCM/float encodings to mono
16 kHz, computes Whisper features once per 30-second audio window, and selects
centered 10-step contexts at the 50 Hz feature rate. Longer turns are processed
as consecutive encoder windows instead of being silently truncated. These
helpers are unit-tested, but they do not enable repair.

A development-only export probe lives at
`tools/dev/model-conversion/export-latentsync-1-5.py`. It exports fixed temporal
shapes and guidance branches, checks the ONNX graph, and hashes the external
data files. `export-latentsync-1-5-components.py` exports the VAE encoder,
decoder, and Whisper encoder. Source and component hashes are recorded in
`latentsync-1-5-component-export-probe.json`. The U-Net graph contract and
operator domains are recorded in `latentsync-1-5-unet16-probe.json`; its 711
external-data tensor hashes are in `latentsync-1-5-unet16-external-data.sha256.json`.

Local sources are pinned to LatentSync code
`a229c3948406bc2cf6eaf4873e662e70c6a04746`, LatentSync 1.5 weights
`32a20d29aead0498e3e885e90dbbe8027da1b61b`, and SD VAE weights
`31f26fdeee1355a5c34592e401dd41e45d25a493`. The generated U-Net uses the
upstream 256-pixel, 16-frame, 20-step, guidance-1.5 profile. The guided graph
inputs are `sample=[2,13,16,32,32]`, `timestep=[2]`, and
`encoder_hidden_states=[32,10,384]`. Its 13 sample channels concatenate four
noisy latent channels, one mask channel, four masked-image latent channels,
and four reference latent channels.

Local runtime probes on an RTX 5070 (12,227 MiB reported total VRAM):

| Probe | Result |
| --- | --- |
| VAE encoder, VAE decoder, Whisper encoder | ONNX Runtime CPU session and zero-input inference passed with outputs `[1,4,32,32]`, `[1,3,256,256]`, and `[1,1500,384]`. |
| One-frame guided U-Net `[2,13,1,32,32]` | DirectML forward pass returned finite `[2,4,1,32,32]` output. One-second `nvidia-smi` sampling peaked at 10,454 MiB total GPU use. |
| Eight-frame guided U-Net | Session loaded; inference failed at `up_blocks.0.upsamplers.0/Resize` with DirectML `80070057`. Sampled peak: 11,319 MiB. |
| Sixteen-frame guided U-Net | Session loaded; inference failed at the same `Resize` node with DirectML `80070057`. Sampled peak: 11,515 MiB. |

The one-frame result is only an operator/runtime spike; it does not prove the
temporal model runs. The 8- and 16-frame failures block the intended temporal
path on this DirectML runtime. This ONNX Runtime build exposed DirectML and CPU
providers, not CUDA. It also warned that some nodes were assigned away from the
preferred provider. Peak GPU values are sampled totals, not isolated
per-process VRAM measurements.

Two experimental graph rewrites of the failing temporal `Resize` also failed
to initialize DirectML sessions (`80070057`): static 5D scales and a manual
flatten/2D resize. Those rewrites were not retained.

The application has no supported per-process VRAM probe, so there is no
memory-based preflight gate today. The stage remains unavailable independently
of reported GPU memory. When a versioned repair path becomes viable, measured
requirements plus headroom belong in runtime planning; an actual allocation
failure should be reported as a failed turn with its original video preserved.
Existing runtime-unavailable and failed outcomes cover those cases; this work
does not add a success-like memory status.

The candidate bundle layout is `unet.onnx`, `vae_encoder.onnx`,
`vae_decoder.onnx`, `whisper_encoder.onnx`, all U-Net external-data files,
`mask.png`, and a versioned `model-config.json` with face/latent sizes, audio
context, guidance, and scheduler settings. The proposed mirror remains
`tonythethompson/latentsync-1.5-onnx`, pinned to an immutable revision after
review; no mirror or 1.5 manifest entry exists yet. The official model card
identifies the weights as `openrail++`; dependency and weights license review
is incomplete, so 1.5 is not approved for commercial default use
([model card](https://huggingface.co/ByteDance/LatentSync-1.5)).

Remaining gates are a DirectML-compatible temporal U-Net or a separately
verified runtime, version-resolved graph input checks, temporal batches,
per-frame face tracking and masked compositing, matching-seed parity, three
5–10 second clip comparisons, license review, and bundle publication. The 1.5
default alias migration waits for all gates; explicit `latentsync-1.6` pins
remain unchanged. Olive work follows a passing unoptimized clip baseline.
TensorRT remains excluded until graph import and runtime tests support it.

For optimization acceptance, record median runtime and peak VRAM across a
frontal speech, head movement, and cross-language 25 fps fixture. Require at
least 10% lower median runtime or peak VRAM, SyncNet score loss no greater
than 0.2, face-crop LPIPS increase no greater than 0.02, PSNR at least 35 dB
against matching-seed unoptimized ONNX output, and zero pre-encode pixel
difference outside the compositing mask.
