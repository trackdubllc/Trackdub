#!/usr/bin/env python3
"""Flip Phi-3.5-mini using pre-verified cpu-int4 hashes."""

from __future__ import annotations

import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
HASHES_PATH = REPO_ROOT / "tools/ci/hashes-phi-35.json"

MODEL_ID = "microsoft/Phi-3.5-mini-instruct-onnx"


def main() -> None:
    payload = json.loads(HASHES_PATH.read_text(encoding="utf-8"))
    hashes = payload["download_file_hashes"]
    revision = payload["revision"]
    base = f"https://huggingface.co/{MODEL_ID}/resolve/{revision}"

    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    for model in catalog["models"]:
        if model["model_id"] != MODEL_ID:
            continue
        model["commercial_use_verified"] = True
        model["download_file_hashes"] = hashes
        model["download_file_sources"] = {path: f"{base}/{path}" for path in hashes}
        model["hash_verification"] = {"mode": "required"}
        benchmark = model.get("benchmark_entry")
        if benchmark and benchmark in hashes:
            model["sha256"] = hashes[benchmark]
        break
    else:
        raise RuntimeError(f"{MODEL_ID} not found")

    with MANIFEST_PATH.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(catalog, handle, indent=2)
        handle.write("\n")
    print(f"Flipped {MODEL_ID}", flush=True)


if __name__ == "__main__":
    main()
