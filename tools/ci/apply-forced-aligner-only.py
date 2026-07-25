#!/usr/bin/env python3
"""Flip forced aligner only (hashes pre-verified)."""

from __future__ import annotations

import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"

REVISION = "4b904d4e0eb18c8dd1726ff5e830f72c06dd665b"
HASHES = {
    "config.json": "91f38394bd8117ad2ccbdfc0942d9c100a7482d4c43688674ef2a40fe64eb061",
    "merges.txt": "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5",
    "onnx/model_q4.onnx": "59b528896d70b34e57838e160d16d5f7cfc02d86c7c6ad46cdc57c25c15497b7",
    "vocab.json": "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910",
}
BASE = f"https://huggingface.co/tonythethompson/Qwen3-ForcedAligner-0.6B-ONNX/resolve/{REVISION}"


def main() -> None:
    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    for model in catalog["models"]:
        if model["model_id"] != "qwen3-forced-aligner-0.6b-q4-onnx":
            continue
        model["revision"] = REVISION
        model["commercial_use_verified"] = True
        model["download_file_hashes"] = HASHES
        model["download_file_sources"] = {k: f"{BASE}/{k}" for k in HASHES}
        model["hash_verification"] = {"mode": "required"}
        model.pop("lane", None)
        model.pop("notes", None)
        break

    with MANIFEST_PATH.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(catalog, handle, indent=2)
        handle.write("\n")
    print("Flipped qwen3-forced-aligner-0.6b-q4-onnx", flush=True)


if __name__ == "__main__":
    main()
