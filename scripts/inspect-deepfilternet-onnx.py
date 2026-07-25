#!/usr/bin/env python3
"""
DeepFilterNet3 ONNX graph inspection script.

Downloads DeepFilterNet3_onnx.tar.gz from GitHub releases, extracts the three
split ONNX graphs (enc.onnx, erb_dec.onnx, df_dec.onnx), prints their input/output
tensor names + shapes + dtypes, and runs a synthetic forward pass to validate
that ONNX Runtime can load and run them (CPU; DML if running on Windows).

Usage:
    pip install onnx onnxruntime requests
    python scripts/inspect-deepfilternet-onnx.py

    # Force a specific output directory (default: models/deepfilternet3):
    python scripts/inspect-deepfilternet-onnx.py --output-dir /path/to/models/deepfilternet3

    # Skip download if files already exist:
    python scripts/inspect-deepfilternet-onnx.py --skip-download
"""

import argparse
import hashlib
import io
import platform
import sys
import tarfile
import urllib.request
from pathlib import Path
from urllib.parse import urlparse

import numpy as np
import onnx
import onnxruntime as ort

# GitHub release URL for DeepFilterNet3 standard ONNX archive (~7.6 MB).
# Source: https://github.com/Rikorose/DeepFilterNet/releases
RELEASE_URL = (
    "https://github.com/Rikorose/DeepFilterNet/releases/download/"
    "pretrained-models-v0.5.6/DeepFilterNet3_onnx.tar.gz"
)

# Standard DeepFilterNet3 signal processing constants.
SAMPLE_RATE = 48000
FRAME_SIZE = 480       # 10ms at 48kHz
FFT_SIZE = 960         # STFT FFT size
FREQ_BINS = 481        # FFT_SIZE // 2 + 1
ERB_BANDS = 32
EMB_DIM = 256

# Number of synthetic frames to use for forward-pass validation.
SYNTH_FRAMES = 20


def download(url: str, dest: Path) -> str:
    parsed = urlparse(url)
    if parsed.scheme != "https":
        raise ValueError(f"Refusing to download DeepFilterNet model archive from non-HTTPS URL: {url}")

    dest.parent.mkdir(parents=True, exist_ok=True)
    print(f"Downloading {url} → {dest.name} ...", flush=True)
    with urllib.request.urlopen(url) as resp:  # noqa: S310 - URL scheme is validated above.
        data = resp.read()
    dest.write_bytes(data)
    sha256 = hashlib.sha256(data).hexdigest()
    print(f"  {len(data):,} bytes  sha256={sha256}")
    return sha256


def extract(tar_path: Path, out_dir: Path) -> list[Path]:
    out_dir.mkdir(parents=True, exist_ok=True)
    extracted = []
    with tarfile.open(tar_path) as tf:
        for member in tf.getmembers():
            if member.name.endswith(".onnx"):
                member.name = Path(member.name).name  # flatten directory
                tf.extract(member, out_dir)
                extracted.append(out_dir / member.name)
                print(f"  Extracted: {member.name}")
    return sorted(extracted)


def inspect_graph(path: Path) -> dict:
    model = onnx.load(str(path))
    opset = {op.domain: op.version for op in model.opset_import}
    inputs = [
        {
            "name": i.name,
            "dtype": onnx.TensorProto.DataType.Name(i.type.tensor_type.elem_type),
            "shape": [
                (d.dim_param if d.dim_param else d.dim_value)
                for d in i.type.tensor_type.shape.dim
            ],
        }
        for i in model.graph.input
    ]
    outputs = [
        {
            "name": o.name,
            "dtype": onnx.TensorProto.DataType.Name(o.type.tensor_type.elem_type),
            "shape": [
                (d.dim_param if d.dim_param else d.dim_value)
                for d in o.type.tensor_type.shape.dim
            ],
        }
        for o in model.graph.output
    ]
    return {"opset": opset, "inputs": inputs, "outputs": outputs}


def make_random_tensor(spec: dict, num_frames: int) -> np.ndarray:
    """Replace any symbolic dimension name with num_frames; keep zeros for unknown sizes."""
    shape = []
    for dim in spec["shape"]:
        if isinstance(dim, str):
            shape.append(num_frames)
        elif isinstance(dim, int) and dim > 0:
            shape.append(dim)
        else:
            shape.append(1)
    return np.random.randn(*shape).astype(np.float32)


def run_session(onnx_path: Path, inputs_spec: list[dict], num_frames: int) -> list[dict]:
    providers = ["CPUExecutionProvider"]
    if platform.system() == "Windows":
        providers = ["DmlExecutionProvider", "CPUExecutionProvider"]

    sess = ort.InferenceSession(str(onnx_path), providers=providers)
    feed = {s["name"]: make_random_tensor(s, num_frames) for s in inputs_spec}
    print(f"  Running with provider={sess.get_providers()[0]} frames={num_frames} ...", end=" ")
    outputs = sess.run(None, feed)
    print("OK")
    return [
        {"name": meta.name, "shape": list(out.shape), "dtype": str(out.dtype)}
        for meta, out in zip(sess.get_outputs(), outputs, strict=True)
    ]


def print_graph(name: str, info: dict) -> None:
    print(f"\n{'=' * 60}")
    print(f"  {name}")
    print(f"  opset: {info['opset']}")
    print()
    print("  INPUTS:")
    for t in info["inputs"]:
        print(f"    {t['name']:30s} {t['dtype']:10s} {t['shape']}")
    print()
    print("  OUTPUTS:")
    for t in info["outputs"]:
        print(f"    {t['name']:30s} {t['dtype']:10s} {t['shape']}")
    print(f"{'=' * 60}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--output-dir", default="models/deepfilternet3")
    ap.add_argument("--skip-download", action="store_true")
    args = ap.parse_args()

    out_dir = Path(args.output_dir)
    tar_path = out_dir / "DeepFilterNet3_onnx.tar.gz"

    if not args.skip_download:
        sha256 = download(RELEASE_URL, tar_path)
        print(f"\nAdd to manifest: \"sha256\": \"{sha256}\"")
    else:
        if not tar_path.exists():
            print(f"ERROR: {tar_path} not found. Remove --skip-download.")
            return 1

    print("\nExtracting ONNX graphs ...")
    onnx_files = extract(tar_path, out_dir)

    if not onnx_files:
        print("ERROR: No .onnx files found in archive.")
        return 1

    graphs: dict[str, dict] = {}
    for path in onnx_files:
        info = inspect_graph(path)
        graphs[path.name] = info
        print_graph(path.name, info)

    print("\n\nFORWARD PASS VALIDATION")
    print("-" * 60)

    any_failure = False
    for path in onnx_files:
        print(f"\n{path.name}:")
        info = graphs[path.name]
        try:
            actual_outputs = run_session(path, info["inputs"], SYNTH_FRAMES)
            for o in actual_outputs:
                print(f"    output {o['name']:30s} {o['dtype']:10s} {o['shape']}")
        except Exception as exc:  # noqa: BLE001 - this validation script must report every graph failure.
            any_failure = True
            print(f"  FAILED: {exc}")

    print("\n\nSUMMARY FOR DeepFilterNetModelPaths.cs")
    print("-" * 60)
    print("// Tensor names and shapes confirmed by scripts/inspect-deepfilternet-onnx.py")
    for name, info in sorted(graphs.items()):
        print(f"// {name}:")
        for t in info["inputs"]:
            print(f"//   input  {t['name']} {t['shape']} {t['dtype']}")
        for t in info["outputs"]:
            print(f"//   output {t['name']} {t['shape']} {t['dtype']}")

    return 1 if any_failure else 0


if __name__ == "__main__":
    sys.exit(main())
