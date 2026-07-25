"""
Export SepFormer (speechbrain/sepformer-whamr16k) and pyannote segmentation-3.0 to ONNX.
Validates both graphs with onnxruntime, then uploads to HuggingFace.

Requirements:
    pip install speechbrain pyannote.audio onnx onnxruntime huggingface_hub torch

Usage:
    # Export and upload (HF token via HF_TOKEN, hf auth login, or ~/.cache/huggingface/token):
    python scripts/export-sepformer-onnx.py

    # OSD runs before SepFormer to avoid SpeechBrain/pyannote import conflicts on Windows.

    # Export only (skip upload):
    python scripts/export-sepformer-onnx.py --no-upload

    # Inspect tensor names only (no export):
    python scripts/export-sepformer-onnx.py --inspect-only
"""

import argparse
import os
import sys
from pathlib import Path

import numpy as np

HF_REPO = "tonythethompson/sepformer-whamr16k-onnx"
SR = 16000
OPSET = 17

SEP_ONNX = "sepformer.onnx"
OSD_ONNX = "osd.onnx"


def print_tensor_info(session, label: str):
    print(f"\n{label} tensor info:")
    for inp in session.get_inputs():
        print(f"  input  '{inp.name}': shape={inp.shape} dtype={inp.type}")
    for out in session.get_outputs():
        print(f"  output '{out.name}': shape={out.shape} dtype={out.type}")


def export_sepformer(no_upload: bool = False):
    # Import ONNX stack before SpeechBrain so torch.onnx registration does not
    # walk a call stack that triggers SpeechBrain's optional k2 lazy import.
    import onnx  # noqa: F401
    import torch.onnx  # noqa: F401
    import torch
    from speechbrain.inference.separation import SepformerSeparation
    import onnxruntime as ort

    print("Loading speechbrain/sepformer-whamr16k ...")
    sep_model = SepformerSeparation.from_hparams(
        source="speechbrain/sepformer-whamr16k",
        savedir="pretrained_models/sepformer-whamr16k",
    )
    sep_model.eval()

    # Inspect native output shape with a short dummy pass before export
    dummy_native = torch.zeros(1, SR * 4)
    with torch.no_grad():
        native_out = sep_model.separate_batch(dummy_native)
    print(f"Native separate_batch output shape: {native_out.shape}  (expected [1, T, 2])")

    num_spks = int(sep_model.hparams.num_spks)

    class SepFormerCore(torch.nn.Module):
        """Pure torch graph from SepformerSeparation.separate_batch (no SpeechBrain calls)."""

        def __init__(self, encoder, masknet, decoder, speakers: int):
            super().__init__()
            self.encoder = encoder
            self.masknet = masknet
            self.decoder = decoder
            self.speakers = speakers

        def forward(self, mix: torch.Tensor):  # [1, T]
            import torch.nn.functional as F

            mix_w = self.encoder(mix)
            est_mask = self.masknet(mix_w)
            mix_w = torch.stack([mix_w] * self.speakers)
            sep_h = mix_w * est_mask
            parts = [self.decoder(sep_h[i]).unsqueeze(-1) for i in range(self.speakers)]
            est_source = torch.cat(parts, dim=-1)  # [1, T', 2]
            t_origin = mix.size(1)
            t_est = est_source.size(1)
            if t_origin > t_est:
                est_source = F.pad(est_source, (0, 0, 0, t_origin - t_est))
            else:
                est_source = est_source[:, :t_origin, :]
            return est_source[0, :, 0], est_source[0, :, 1]

    wrapper = SepFormerCore(
        sep_model.mods.encoder,
        sep_model.mods.masknet,
        sep_model.mods.decoder,
        num_spks,
    ).eval()

    # Use a 4-second dummy; SepFormer processes variable lengths.
    # Pad to nearest multiple of encoder stride if needed.
    dummy = torch.zeros(1, SR * 4)

    print(f"\nExporting {SEP_ONNX} (opset {OPSET}) ...")
    try:
        torch.onnx.export(
            wrapper,
            dummy,
            SEP_ONNX,
            input_names=["mix"],
            output_names=["source_0", "source_1"],
            dynamic_axes={
                "mix":      {1: "time"},
                "source_0": {0: "time"},
                "source_1": {0: "time"},
            },
            opset_version=OPSET,
            dynamo=False,
        )
        print(f"Export succeeded: {SEP_ONNX}")
    except Exception as e:
        print(f"Single-graph export failed: {e}")
        print("Consider 3-subgraph split (encoder -> masknet -> decoder).")
        raise

    # Validate
    providers = ["CPUExecutionProvider"]
    sess = ort.InferenceSession(SEP_ONNX, providers=providers)
    print_tensor_info(sess, "SepFormer")
    test_input = {"mix": np.zeros((1, SR * 2), dtype=np.float32)}
    s0, s1 = sess.run(None, test_input)
    assert s0.shape == (SR * 2,), f"source_0 shape mismatch: {s0.shape}"
    assert s1.shape == (SR * 2,), f"source_1 shape mismatch: {s1.shape}"
    print(f"SepFormer validation OK: source_0={s0.shape} source_1={s1.shape}")


def export_osd():
    # Import before SpeechBrain — pyannote/lightning can trigger torch._dynamo stack
    # walks that break if speechbrain is already loaded on Windows.
    import onnx  # noqa: F401
    import torch.onnx  # noqa: F401
    import torch
    from pyannote.audio import Model
    import onnxruntime as ort

    hf_token = os.environ.get("HF_TOKEN")
    if not hf_token:
        token_path = os.path.join(os.path.expanduser("~"), ".cache", "huggingface", "token")
        if os.path.isfile(token_path):
            hf_token = Path(token_path).read_text(encoding="utf-8").strip()
    if not hf_token:
        print("WARNING: HF_TOKEN not set — pyannote/segmentation-3.0 requires gated access.")
        print("  Visit https://huggingface.co/pyannote/segmentation-3.0 and accept the license,")
        print("  then set HF_TOKEN=hf_... or run: hf auth login")
        sys.exit(1)

    print("\nLoading pyannote/segmentation-3.0 ...")
    try:
        osd_model = Model.from_pretrained("pyannote/segmentation-3.0", token=hf_token)
    except TypeError:
        osd_model = Model.from_pretrained("pyannote/segmentation-3.0", use_auth_token=hf_token)
    osd_model.eval()

    # pyannote segmentation: [1, T] → [1, frames, num_classes]
    # class index 3 = overlap (non-speech=0, spk1=1, spk2=2, overlap=3)
    # Native window is 10 seconds; use dynamic axes for variable-length audio.
    dummy_osd = torch.zeros(1, SR * 10)

    with torch.no_grad():
        native_seg = osd_model(dummy_osd)
    print(f"Native segmentation output shape: {native_seg.shape}  (expected [1, ~293, num_classes])")

    print(f"\nExporting {OSD_ONNX} (opset {OPSET}) ...")
    torch.onnx.export(
        osd_model,
        dummy_osd,
        OSD_ONNX,
        input_names=["waveform"],
        output_names=["segmentation"],
        dynamic_axes={
            "waveform":     {1: "time"},
            "segmentation": {1: "frames"},
        },
        opset_version=OPSET,
        dynamo=False,
    )
    print(f"Export succeeded: {OSD_ONNX}")

    # Validate
    providers = ["CPUExecutionProvider"]
    sess_osd = ort.InferenceSession(OSD_ONNX, providers=providers)
    print_tensor_info(sess_osd, "OSD")
    seg = sess_osd.run(None, {"waveform": np.zeros((1, SR * 10), dtype=np.float32)})
    print(f"OSD validation OK: segmentation={seg[0].shape}")

    num_classes = seg[0].shape[2]
    if num_classes < 4:
        print(f"WARNING: expected >=4 classes for overlap at index 3, got {num_classes}")
        print("  Verify overlap class index in C# SepFormerOnnxSeparator.cs")
    else:
        print(f"Overlap class index 3 OK (num_classes={num_classes})")


def upload_to_hf():
    from huggingface_hub import HfApi

    print("\nUploading to HuggingFace ...")
    api = HfApi()
    api.create_repo(HF_REPO, exist_ok=True, repo_type="model")
    for fname in [SEP_ONNX, OSD_ONNX]:
        if not os.path.exists(fname):
            print(f"SKIP: {fname} not found locally")
            continue
        api.upload_file(
            path_or_fileobj=fname,
            path_in_repo=fname,
            repo_id=HF_REPO,
            repo_type="model",
        )
        print(f"Uploaded {fname} → huggingface.co/{HF_REPO}")

    print("\nFetch repo info for revision + sha256:")
    info = api.repo_info(HF_REPO, repo_type="model")
    print(f"  Repo: {info.id}")
    print(f"  SHA (revision): {info.sha}")
    print()
    print("Update bundled-models.manifest.json:")
    print(f'  "revision": "{info.sha}",')
    for sibling in info.siblings or []:
        if sibling.rfilename in (SEP_ONNX, OSD_ONNX):
            print(f'  {sibling.rfilename} blob: {sibling.blob_id}')


def inspect_only():
    """Load already-exported ONNX files and print tensor info."""
    import onnxruntime as ort

    providers = ["CPUExecutionProvider"]
    for fname in [SEP_ONNX, OSD_ONNX]:
        if not os.path.exists(fname):
            print(f"File not found: {fname} (run without --inspect-only first)")
            continue
        sess = ort.InferenceSession(fname, providers=providers)
        print_tensor_info(sess, fname)


def main():
    # Warm ONNX registration before any SpeechBrain import paths run.
    import onnx  # noqa: F401
    import torch.onnx  # noqa: F401

    parser = argparse.ArgumentParser(description="Export SepFormer + OSD to ONNX")
    parser.add_argument("--no-upload", action="store_true", help="Skip HuggingFace upload")
    parser.add_argument("--inspect-only", action="store_true",
                        help="Only inspect already-exported ONNX files")
    parser.add_argument("--skip-osd", action="store_true",
                        help="Skip pyannote OSD export (requires HF gated access)")
    parser.add_argument("--skip-sepformer", action="store_true",
                        help="Skip SepFormer export (e.g. osd-only re-run)")
    args = parser.parse_args()

    if args.inspect_only:
        inspect_only()
        return

    if not args.skip_osd:
        export_osd()
    else:
        print(f"\nSkipping OSD export (--skip-osd). {OSD_ONNX} will not be uploaded.")

    if not args.skip_sepformer:
        export_sepformer(no_upload=args.no_upload)
    else:
        print(f"\nSkipping SepFormer export (--skip-sepformer).")

    if not args.no_upload:
        upload_to_hf()
    else:
        print(f"\nSkipping upload (--no-upload). Files written locally: {SEP_ONNX}, {OSD_ONNX}")

    print("\nDone. Record tensor names/shapes above before writing C# SepFormerOnnxSeparator.cs")


if __name__ == "__main__":
    main()
