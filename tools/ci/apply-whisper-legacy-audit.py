#!/usr/bin/env python3
"""Flip legacy whisper-onnx manifest entries after HF hash verification."""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from manifest_hash_io import sha256_url

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
HASH_CACHE_PATH = REPO_ROOT / "tools/ci/hashes-whisper-legacy.json"

REVISION_OVERRIDES = {
    "onnx-community/whisper-base": "1846881b6b3a3024392c1eea3ad983695bc23925",
    "onnx-community/whisper-small": "36050c46d777d46dc4b5f43f6d90574fc38f8732",
}

ONNX_FILES = ("onnx/encoder_model.onnx", "onnx/decoder_model.onnx")

TARGET_IDS = (
    "onnx-community/whisper-base",
    "onnx-community/whisper-small",
    "Xenova/whisper-medium",
    "Xenova/whisper-large-v3",
)


def resolve_url(model: dict, file_path: str) -> str:
    revision = model["revision"]
    source_url = str(model["source_url"]).rstrip("/")
    return f"{source_url}/resolve/{revision}/{file_path}"


def collect_hash_paths(model: dict) -> list[str]:
    paths = list(model.get("download_files") or [])
    for onnx_path in ONNX_FILES:
        if onnx_path not in paths:
            paths.append(onnx_path)

    for variant in model.get("variants") or []:
        if variant.get("alias") == "default" or variant.get("is_default"):
            for path in variant.get("download_files") or []:
                paths.append(path)
            entry_path = variant.get("entry_path")
            if entry_path:
                paths.append(entry_path)
            break

    benchmark = model.get("benchmark_entry")
    if benchmark and benchmark not in paths:
        paths.append(benchmark)
    return sorted(set(paths))


def load_cache() -> dict[str, dict[str, str]]:
    if not HASH_CACHE_PATH.is_file():
        return {}
    with HASH_CACHE_PATH.open(encoding="utf-8") as handle:
        return json.load(handle)


def save_cache(cache: dict[str, dict[str, str]]) -> None:
    with HASH_CACHE_PATH.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(cache, handle, indent=2)
        handle.write("\n")


def hash_model(model: dict, cache: dict[str, dict[str, str]]) -> dict[str, str]:
    model_id = model["model_id"]
    if model_id in cache and len(cache[model_id]) == len(collect_hash_paths(model)):
        print(f"Using cached hashes for {model_id}")
        return cache[model_id]

    results: dict[str, str] = {}
    for file_path in collect_hash_paths(model):
        url = resolve_url(model, file_path)
        print(f"Hashing {model_id} :: {file_path}")
        results[file_path] = sha256_url(url, label=file_path)

    cache[model_id] = results
    save_cache(cache)
    return results


def apply_flip(model: dict, file_hashes: dict[str, str]) -> None:
    model["commercial_use_verified"] = True
    model["download_file_hashes"] = dict(file_hashes)
    model["hash_verification"] = {"mode": "required"}

    download_files = set(model.get("download_files") or [])
    download_files.update(file_hashes.keys())
    model["download_files"] = sorted(download_files)

    sources = dict(model.get("download_file_sources") or {})
    revision = model["revision"]
    source_url = str(model["source_url"]).rstrip("/")
    for path in file_hashes:
        sources[path] = f"{source_url}/resolve/{revision}/{path}"
    model["download_file_sources"] = sources

    benchmark = model.get("benchmark_entry")
    if benchmark and benchmark in file_hashes:
        model["sha256"] = file_hashes[benchmark]


def main() -> None:
    cache = load_cache()

    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    for model in catalog["models"]:
        model_id = model["model_id"]
        if model_id not in TARGET_IDS:
            continue

        if model_id in REVISION_OVERRIDES:
            model["revision"] = REVISION_OVERRIDES[model_id]

        if not model.get("revision"):
            raise SystemExit(f"{model_id}: missing revision")

        file_hashes = hash_model(model, cache)
        apply_flip(model, file_hashes)
        print(f"Flipped {model_id} ({len(file_hashes)} hashes)")

    with MANIFEST_PATH.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(catalog, handle, indent=2)
        handle.write("\n")

    print(f"Updated {MANIFEST_PATH}")


if __name__ == "__main__":
    main()
