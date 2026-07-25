#!/usr/bin/env python3
"""Compute download_file_hashes and download_file_sources for Qwen3 TTS manifest entries."""

from __future__ import annotations

import hashlib
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

MODELS: dict[str, list[str]] = {
    "tonythethompson/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX": [],
    "tonythethompson/Qwen3-TTS-12Hz-0.6B-Base-ONNX": [],
    "tonythethompson/Qwen3-TTS-12Hz-1.7B-CustomVoice-ONNX": [],
    "tonythethompson/Qwen3-TTS-12Hz-1.7B-Base-ONNX": [],
}

SKIP_PREFIXES = (".git",)
SKIP_NAMES = {"README.md", ".gitattributes"}


def api_json(url: str) -> object:
    with urllib.request.urlopen(url, timeout=120) as response:
        return json.load(response)


def model_revision(model_id: str) -> str:
    payload = api_json(f"https://huggingface.co/api/models/{model_id}")
    return str(payload["sha"])


def walk_files(model_id: str, path: str = "") -> list[str]:
    suffix = f"/{path}" if path else ""
    url = f"https://huggingface.co/api/models/{model_id}/tree/main{suffix}"
    items = api_json(url)
    files: list[str] = []
    for item in items:
        name = item["path"]
        if item["type"] == "file":
            if name in SKIP_NAMES or any(name.startswith(prefix) for prefix in SKIP_PREFIXES):
                continue
            files.append(name)
        else:
            files.extend(walk_files(model_id, name))
    return sorted(files)


def sha256_url(url: str) -> str:
    digest = hashlib.sha256()
    with urllib.request.urlopen(url, timeout=600) as response:
        while True:
            chunk = response.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def build(model_id: str) -> dict:
    revision = model_revision(model_id)
    files = walk_files(model_id)
    hashes: dict[str, str] = {}
    sources: dict[str, str] = {}
    base = f"https://huggingface.co/{model_id}/resolve/{revision}/"
    for index, file_path in enumerate(files, start=1):
        url = base + file_path
        print(f"[{model_id}] ({index}/{len(files)}) {file_path}", flush=True)
        hashes[file_path] = sha256_url(url)
        sources[file_path] = url
    return {
        "model_id": model_id,
        "revision": revision,
        "benchmark_entry": "talker_prefill.onnx",
        "download_files": files,
        "download_file_hashes": hashes,
        "download_file_sources": sources,
        "sha256": hashes.get("talker_prefill.onnx"),
    }


def main() -> None:
    out_dir = REPO_ROOT / "tools/ci"
    for model_id in MODELS:
        payload = build(model_id)
        slug = model_id.split("/")[-1].lower().replace(".", "-")
        out_path = out_dir / f"hashes-{slug}.json"
        out_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        print(f"Wrote {out_path}", flush=True)


if __name__ == "__main__":
    main()
