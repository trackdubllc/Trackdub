#!/usr/bin/env python3
"""Insert or update Qwen3 TTS manifest entries from tools/ci/hashes-*.json evidence."""

from __future__ import annotations

import json
from copy import deepcopy
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
CI_DIR = REPO_ROOT / "tools/ci"

ATTRIBUTION_NOTE = (
    "Runtime integration is tracked separately; this notice records the published ONNX bundle and license metadata only."
)

ENTRIES = [
    {
        "model_id": "tonythethompson/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
        "hash_file": "hashes-qwen3-tts-12hz-0-6b-customvoice-onnx.json",
        "fallback_hash_file": "hashes-qwen3-tts.json",
        "tier": "balanced",
        "display_name": "Qwen3-TTS 0.6B CustomVoice",
        "root_path": "../../../../models/qwen3-tts-0.6b-customvoice",
        "aliases": ["qwen3-tts-0.6b-customvoice", "qwen3-tts-0.6b", "qwen3-tts", "qwen-tts"],
        "capabilities": ["tts", "preset-voices", "style-control"],
        "requires_user_consent": False,
        "voice_cloning": False,
        "notes": ATTRIBUTION_NOTE,
    },
    {
        "model_id": "tonythethompson/Qwen3-TTS-12Hz-1.7B-CustomVoice-ONNX",
        "hash_file": "hashes-qwen3-tts-12hz-1-7b-customvoice-onnx.json",
        "tier": "quality",
        "display_name": "Qwen3-TTS 1.7B CustomVoice",
        "root_path": "../../../../models/qwen3-tts-1.7b-customvoice",
        "aliases": ["qwen3-tts-1.7b-customvoice", "qwen3-tts-1.7b"],
        "capabilities": ["tts", "preset-voices", "style-control"],
        "requires_user_consent": False,
        "voice_cloning": False,
        "notes": f"Large bundle; CPU verification path only. {ATTRIBUTION_NOTE}",
    },
    {
        "model_id": "tonythethompson/Qwen3-TTS-12Hz-0.6B-Base-ONNX",
        "hash_file": "hashes-qwen3-tts-12hz-0-6b-base-onnx.json",
        "tier": "balanced",
        "display_name": "Qwen3-TTS 0.6B Base",
        "root_path": "../../../../models/qwen3-tts-0.6b-base",
        "aliases": ["qwen3-tts-0.6b-base"],
        "capabilities": ["tts", "voice-clone"],
        "requires_user_consent": True,
        "voice_cloning": True,
        "notes": ATTRIBUTION_NOTE,
    },
    {
        "model_id": "tonythethompson/Qwen3-TTS-12Hz-1.7B-Base-ONNX",
        "hash_file": "hashes-qwen3-tts-12hz-1-7b-base-onnx.json",
        "tier": "quality",
        "display_name": "Qwen3-TTS 1.7B Base",
        "root_path": "../../../../models/qwen3-tts-1.7b-base",
        "aliases": ["qwen3-tts-1.7b-base"],
        "capabilities": ["tts", "voice-clone"],
        "requires_user_consent": True,
        "voice_cloning": True,
        "notes": f"Large bundle; CPU verification path only. {ATTRIBUTION_NOTE}",
    },
]


def load_hash_payload(entry: dict) -> dict:
    for name in (entry["hash_file"], entry.get("fallback_hash_file")):
        if not name:
            continue
        path = CI_DIR / name
        if path.exists():
            return json.loads(path.read_text(encoding="utf-8"))
    raise FileNotFoundError(f"No hash evidence for {entry['model_id']}")


def build_model(entry: dict, payload: dict) -> dict:
    model_id = entry["model_id"]
    revision = payload["revision"]
    download_files = payload.get("download_files") or sorted(payload["download_file_hashes"].keys())
    hashes = payload["download_file_hashes"]
    sources = payload.get("download_file_sources") or {
        path: f"https://huggingface.co/{model_id}/resolve/{revision}/{path}" for path in download_files
    }
    benchmark = payload.get("benchmark_entry") or "talker_prefill.onnx"
    return {
        "model_id": model_id,
        "task": "tts",
        "engine_family": "qwen3-tts",
        "capabilities": entry["capabilities"],
        "language_coverage": {"target_languages": ["multi"]},
        "tier": entry["tier"],
        "license": "Apache-2.0",
        "commercial_allowed": True,
        "redistribution_allowed": True,
        "requires_attribution": True,
        "requires_user_consent": entry["requires_user_consent"],
        "voice_cloning": entry["voice_cloning"],
        "commercial_use_verified": False,
        "lane": "experimental",
        "notes": entry["notes"],
        "source_url": f"https://huggingface.co/{model_id}",
        "revision": revision,
        "sha256": hashes.get(benchmark) or payload.get("sha256"),
        "aliases": entry["aliases"],
        "root_path": entry["root_path"],
        "display_name": entry["display_name"],
        "benchmark_entry": benchmark,
        "download_files": download_files,
        "variants": [
            {
                "alias": "default",
                "entry_path": benchmark,
                "is_default": True,
            }
        ],
        "download_file_hashes": hashes,
        "hash_verification": {"mode": "required"},
        "download_file_sources": sources,
    }


def main() -> None:
    catalog = json.loads(MANIFEST_PATH.read_text(encoding="utf-8-sig"))
    models = catalog["models"]
    built = [build_model(entry, load_hash_payload(entry)) for entry in ENTRIES]
    built_ids = {m["model_id"] for m in built}
    models = [m for m in models if m.get("model_id") not in built_ids]
    insert_at = next(
        (index for index, model in enumerate(models) if model.get("model_id") == "Rikorose/DeepFilterNet3"),
        len(models),
    )
    for offset, model in enumerate(built):
        models.insert(insert_at + offset, model)
    catalog["models"] = models
    MANIFEST_PATH.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")
    print(f"Updated manifest with {len(built)} Qwen3 TTS entries at index {insert_at}")


if __name__ == "__main__":
    main()
