#!/usr/bin/env python3
"""Build hash evidence JSON from local models/ bundles (fast path when HF cache is populated)."""

from __future__ import annotations

import hashlib
import json
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MODELS_ROOT = REPO_ROOT / "models"

LOCAL_BUNDLES: dict[str, str] = {
    "tonythethompson/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX": "qwen3-tts-0.6b-customvoice",
    "tonythethompson/Qwen3-TTS-12Hz-0.6B-Base-ONNX": "qwen3-tts-0.6b-base",
    "tonythethompson/Qwen3-TTS-12Hz-1.7B-CustomVoice-ONNX": "qwen3-tts-1.7b-customvoice",
    "tonythethompson/Qwen3-TTS-12Hz-1.7B-Base-ONNX": "qwen3-tts-1.7b-base",
}

SKIP_NAMES = {"README.md", ".gitattributes"}
SKIP_PREFIXES = (".git", ".cache")


def api_revision(model_id: str) -> str:
    with urllib.request.urlopen(f"https://huggingface.co/api/models/{model_id}", timeout=120) as response:
        payload = json.load(response)
    return str(payload["sha"])


def walk_local(root: Path) -> list[str]:
    files: list[str] = []
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in SKIP_NAMES or any(relative.startswith(prefix) for prefix in SKIP_PREFIXES):
            continue
        files.append(relative)
    return files


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def build(model_id: str, bundle_dir: Path) -> dict:
    revision = api_revision(model_id)
    files = walk_local(bundle_dir)
    hashes = {relative: sha256_file(bundle_dir / relative) for relative in files}
    sources = {
        relative: f"https://huggingface.co/{model_id}/resolve/{revision}/{relative}" for relative in files
    }
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
    for model_id, bundle_name in LOCAL_BUNDLES.items():
        bundle_dir = MODELS_ROOT / bundle_name
        if not bundle_dir.exists():
            raise FileNotFoundError(f"Missing local bundle: {bundle_dir}")
        payload = build(model_id, bundle_dir)
        slug = model_id.split("/")[-1].lower().replace(".", "-")
        out_path = out_dir / f"hashes-{slug}.json"
        out_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        print(f"Wrote {out_path} ({len(payload['download_files'])} files)", flush=True)


if __name__ == "__main__":
    main()
