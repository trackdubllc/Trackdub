#!/usr/bin/env python3
"""Flip Phi and Qwen3 TTS after full hash verification."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from manifest_hash_io import configure_unbuffered_stdout, emit, open_log, sha256_url

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
DEFAULT_LOG = REPO_ROOT / "tools/ci/apply-remaining.log"

TARGETS = {
    "microsoft/Phi-3.5-mini-instruct-onnx",
    "microsoft/Phi-4-mini-instruct-onnx",
    "microsoft/phi-4-onnx",
    "tonythethompson/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
}


def resolve_url(model: dict, file_path: str) -> str:
    revision = model["revision"]
    source_url = model["source_url"].rstrip("/")
    return f"{source_url}/resolve/{revision}/{file_path}"


def variant_files(model: dict) -> list[str]:
    for preferred in ("cpu-int4", "default"):
        for variant in model.get("variants") or []:
            if variant.get("alias") == preferred and variant.get("download_files"):
                return list(variant["download_files"])
    for variant in model.get("variants") or []:
        if variant.get("download_files"):
            return list(variant["download_files"])
    return list(model.get("download_files") or [])


def hash_model_files(model: dict, files: list[str], log) -> dict[str, str]:
    results: dict[str, str] = {}
    model_id = model["model_id"]
    emit(f"Hashing {model_id} ({len(files)} files)...", log=log)
    for file_path in files:
        url = resolve_url(model, file_path)
        digest = sha256_url(url, label=file_path, log=log)
        results[file_path] = digest
        emit(f"  {file_path}: {digest}", log=log)
    return results


def build_sources(model_id: str, revision: str, files: list[str], *, repo: str | None = None) -> dict[str, str]:
    base = f"https://huggingface.co/{repo or model_id}/resolve/{revision}"
    return {path: f"{base}/{path}" for path in files}


def apply_flip(model: dict, file_hashes: dict[str, str], *, sources: dict[str, str] | None = None) -> None:
    model["commercial_use_verified"] = True
    model["download_file_hashes"] = file_hashes
    model["hash_verification"] = {"mode": "required"}
    if sources:
        model["download_file_sources"] = sources
    model.pop("lane", None)
    if "notes" in model:
        notes = model["notes"]
        if isinstance(notes, str) and any(
            token in notes.lower()
            for token in ("pending", "experimental", "sign-off", "commercial-use review")
        ):
            model.pop("notes", None)
    benchmark = model.get("benchmark_entry")
    if benchmark and benchmark in file_hashes:
        model["sha256"] = file_hashes[benchmark]


def main() -> None:
    configure_unbuffered_stdout()
    parser = argparse.ArgumentParser()
    parser.add_argument("--log-file", type=Path, default=DEFAULT_LOG)
    args = parser.parse_args()

    with open_log(args.log_file) as log:
        emit(f"=== apply-remaining start ({args.log_file})", log=log)

        with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
            catalog = json.load(handle)

        flipped: list[str] = []
        for model in catalog["models"]:
            model_id = model["model_id"]
            if model_id not in TARGETS:
                continue

            if model.get("commercial_use_verified") is True and model.get("download_file_hashes"):
                emit(f"Skip {model_id} (already verified)", log=log)
                continue

            files = variant_files(model)
            if not files:
                raise RuntimeError(f"No files to hash for {model_id}")

            hashes = hash_model_files(model, files, log)
            sources = build_sources(model_id, model["revision"], files)
            apply_flip(model, hashes, sources=sources)
            flipped.append(model_id)
            emit(f"Flipped {model_id}", log=log)

        with MANIFEST_PATH.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(catalog, handle, indent=2)
            handle.write("\n")

        emit(f"Updated {MANIFEST_PATH} ({len(flipped)} models)", log=log)


if __name__ == "__main__":
    main()
