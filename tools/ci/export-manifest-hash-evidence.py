#!/usr/bin/env python3
"""Export download_file_hashes from bundled manifest to standalone evidence JSON."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
OUT_DIR = REPO_ROOT / "tools/ci"

EVIDENCE_FILENAMES = {
    "microsoft/Phi-3.5-mini-instruct-onnx": "hashes-phi-35.json",
    "microsoft/Phi-4-mini-instruct-onnx": "hashes-phi-4-mini.json",
    "microsoft/phi-4-onnx": "hashes-phi-4.json",
    "tonythethompson/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX": "hashes-qwen3-tts.json",
    "qwen3-forced-aligner-0.6b-q4-onnx": "hashes-forced-aligner.json",
}


def evidence_filename(model_id: str) -> str:
    if model_id in EVIDENCE_FILENAMES:
        return EVIDENCE_FILENAMES[model_id]
    return f"hashes-{slug(model_id)}.json"


def slug(model_id: str) -> str:
    return model_id.split("/")[-1].lower().replace(".", "-")


def export_model(catalog: dict, model_id: str) -> Path:
    for model in catalog["models"]:
        if model["model_id"] != model_id:
            continue
        hashes = model.get("download_file_hashes")
        if not hashes:
            raise RuntimeError(f"{model_id} has no download_file_hashes")
        payload = {
            "model_id": model_id,
            "revision": model["revision"],
            "variant_alias": next(
                (
                    variant.get("alias")
                    for variant in model.get("variants") or []
                    if variant.get("download_files")
                    and set(variant["download_files"]) == set(hashes)
                ),
                None,
            ),
            "benchmark_entry": model.get("benchmark_entry"),
            "download_file_hashes": hashes,
        }
        out_path = OUT_DIR / evidence_filename(model_id)
        out_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        return out_path

    raise RuntimeError(f"{model_id} not found")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-id", action="append", required=True)
    args = parser.parse_args()

    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    for model_id in args.model_id:
        path = export_model(catalog, model_id)
        print(path, flush=True)


if __name__ == "__main__":
    main()
