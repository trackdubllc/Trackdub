#!/usr/bin/env python3
"""Compute SHA-256 for manifest download_files at pinned revisions."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from manifest_hash_io import configure_unbuffered_stdout, emit, sha256_url

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"


def collect_files(model: dict) -> list[str]:
    files: set[str] = set()
    for path in model.get("download_files") or []:
        files.add(path)

    variants = model.get("variants") or []
    variant_files: list[str] = []
    for preferred in ("default", "cpu-int4"):
        for variant in variants:
            if variant.get("alias") == preferred and variant.get("download_files"):
                variant_files = list(variant["download_files"])
                break
        if variant_files:
            break
    if not variant_files:
        for variant in variants:
            if variant.get("download_files"):
                variant_files = list(variant["download_files"])
                break
    for path in variant_files:
        files.add(path)

    for variant in variants:
        entry_path = variant.get("entry_path")
        if entry_path:
            files.add(entry_path)

    benchmark = model.get("benchmark_entry")
    if benchmark:
        files.add(benchmark)
    return sorted(files)


def resolve_url(model: dict, file_path: str) -> str | None:
    sources = model.get("download_file_sources") or {}
    if file_path in sources:
        return sources[file_path]

    revision = model.get("revision")
    source_url = model.get("source_url", "")
    if not revision or revision == "cache-installed" or not source_url:
        return None

    return f"{source_url.rstrip('/')}/resolve/{revision}/{file_path}"


def main() -> None:
    configure_unbuffered_stdout()
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-id", action="append", required=True)
    args = parser.parse_args()

    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    for model in catalog["models"]:
        if model["model_id"] not in args.model_id:
            continue

        emit(f"=== {model['model_id']}")
        results: dict[str, str] = {}
        for file_path in collect_files(model):
            url = resolve_url(model, file_path)
            if not url:
                emit(f"  SKIP {file_path}: no URL")
                continue
            digest = sha256_url(url, label=file_path)
            results[file_path] = digest
            emit(f"  {file_path}: {digest}")

        emit(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
