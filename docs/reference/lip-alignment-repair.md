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
repair engine is unavailable: its older renderer operates on individual frames
and writes over the whole frame, whereas the upstream 1.5 U-Net takes a
13-channel, five-dimensional temporal input. The guard preserves original
video and prevents an unvalidated render from being reported as repaired.
Neither a registered provider nor downloaded weights means repair succeeded.

A development-only export probe lives at
`tools/dev/model-conversion/export-latentsync-1-5.py`. It exports a fixed
frame-count 1.5 U-Net graph, validates the ONNX graph, inspects node domains
and tensor shapes, and hashes all external-data files. A one-frame and a
16-frame graph were exported locally from upstream 1.5 weights; the latter
takes `sample=[1,13,16,32,32]`, `timestep=[1]`, and
`encoder_hidden_states=[16,10,384]`. It loaded and ran one forward pass with
ONNX Runtime DirectML on a 12 GB RTX 5070. This is a feasibility probe, not a
full diffusion run or quality acceptance. CUDA EP could not load on the probe
machine because its CUDA/cuDNN runtime libraries were unavailable.

The 1.5 ONNX bundle still needs the VAE encoder, VAE decoder, and Whisper
encoder graphs, immutable mirror revision and per-file hashes, dependency and
weights license audit, face tracking and masked compositing, conditioned
temporal DDIM loop, validated audio normalization, and three 5–10 second clip
comparisons. Only then can `latentsync` migrate from 1.6 to 1.5 and be marked
commercial default. Explicit `latentsync-1.6` pins remain valid. Olive
optimization follows a passing unoptimized clip baseline; the existing
TensorRT exclusion remains until graph import and runtime tests support it.

For optimization acceptance, record median runtime and peak VRAM across a
frontal speech, head movement, and cross-language 25 fps fixture. Require at
least 10% lower median runtime or peak VRAM, SyncNet score loss no greater
than 0.2, face-crop LPIPS increase no greater than 0.02, PSNR at least 35 dB
against matching-seed unoptimized ONNX output, and zero pre-encode pixel
difference outside the compositing mask.
