#!/usr/bin/env python3
"""Verify bundled manifest artifact SHA-256 against Hugging Face resolve URLs."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

from manifest_schema_common import (
    audited_manifest_model_ids,
    hash_required_paths,
    is_valid_sha256,
    resolve_url,
)

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"

FAMILY_MODELS: dict[str, list[str]] = {
    "opus-mt-onnx-community": [
        "onnx-community/opus-mt-en-es",
        "onnx-community/opus-mt-es-en",
        "onnx-community/opus-mt-en-fr",
        "onnx-community/opus-mt-en-de",
        "onnx-community/opus-mt-en-it",
        "onnx-community/opus-mt-en-ROMANCE",
    ],
    "opus-mt-xenova": [
        "Xenova/opus-mt-es-fr",
        "Xenova/opus-mt-es-de",
        "Xenova/opus-mt-es-it",
    ],
    "phi-genai": [
        "microsoft/Phi-3.5-mini-instruct-onnx",
        "microsoft/Phi-4-mini-instruct-onnx",
        "microsoft/phi-4-onnx",
    ],
    "madlad400": ["google/madlad400-3b-mt"],
    "qwen2.5-instruct": ["tonythethompson/Qwen2.5-1.5B-Instruct"],
    "sortformer": ["cgus/diar_streaming_sortformer_4spk-v2.1-onnx"],
    "whisper-genai": ["openai/whisper-medium", "openai/whisper-large-v3"],
    "whisper-legacy-onnx": [
        "onnx-community/whisper-base",
        "onnx-community/whisper-small",
        "Xenova/whisper-medium",
        "Xenova/whisper-large-v3",
    ],
    "qwen3-asr": [
        "tonythethompson/qwen3-asr-0.6b-onnx",
        "tonythethompson/qwen3-asr-1.7b-onnx",
    ],
    "chatterbox": [
        "ResembleAI/chatterbox-turbo-ONNX",
        "onnx-community/chatterbox-ONNX",
        "onnx-community/chatterbox-multilingual-ONNX",
    ],
    "deepfilternet3": ["Rikorose/DeepFilterNet3"],
    "qwen3-tts": [
        "tonythethompson/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
        "tonythethompson/Qwen3-TTS-12Hz-1.7B-CustomVoice-ONNX",
        "tonythethompson/Qwen3-TTS-12Hz-0.6B-Base-ONNX",
        "tonythethompson/Qwen3-TTS-12Hz-1.7B-Base-ONNX",
    ],
    "cosyvoice": [
        "tonythethompson/CosyVoice-300M-ONNX",
    ],
    "qwen3-forced-aligner-experimental": [
        "qwen3-forced-aligner-0.6b-q4-onnx",
    ],
}


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def files_to_verify(model: dict) -> list[str]:
    hashes = model.get("download_file_hashes")
    if isinstance(hashes, dict) and hashes:
        return sorted(hashes.keys())

    download_files = model.get("download_files")
    if isinstance(download_files, list) and download_files:
        return list(download_files)

    benchmark = model.get("benchmark_entry")
    return [benchmark] if benchmark else []


def expected_hash(model: dict, file_path: str) -> str | None:
    hashes = model.get("download_file_hashes") or {}
    if file_path in hashes:
        return str(hashes[file_path]).lower()

    benchmark = model.get("benchmark_entry")
    top = model.get("sha256")
    if benchmark and file_path == benchmark and top:
        return str(top).lower()

    return None


def fetch_hash(url: str, timeout: int = 600) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "Trackdub-manifest-hash-audit/1.0"})
    digest = hashlib.sha256()
    with urllib.request.urlopen(request, timeout=timeout) as response:
        while True:
            chunk = response.read(8 * 1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def verify_model_structural(model: dict) -> tuple[bool, list[str]]:
    model_id = model.get("model_id", "<unknown>")
    lines: list[str] = [f"[{model_id}] structural"]
    ok = True

    hash_mode = str((model.get("hash_verification") or {}).get("mode", "none")).lower()
    hashes = model.get("download_file_hashes") or {}

    if hash_mode == "required":
        for required_path in hash_required_paths(model):
            if required_path not in hashes:
                lines.append(f"  FAIL missing download_file_hashes['{required_path}']")
                ok = False
            elif not is_valid_sha256(str(hashes[required_path])):
                lines.append(f"  FAIL invalid SHA-256 for '{required_path}'")
                ok = False

    benchmark = model.get("benchmark_entry")
    top_sha = str(model.get("sha256", "")).strip()
    if benchmark and top_sha and benchmark in hashes:
        if str(hashes[benchmark]).lower() != top_sha.lower():
            lines.append(
                f"  FAIL sha256 ({top_sha}) != download_file_hashes['{benchmark}'] ({hashes[benchmark]})"
            )
            ok = False

    for file_path in files_to_verify(model):
        if file_path not in hashes and hash_mode == "required":
            continue
        url = resolve_url(model, file_path)
        if not url:
            lines.append(
                f"  WARN {file_path}: cannot build resolve URL (revision={model.get('revision')})"
            )
            if hash_mode == "required":
                ok = False
        else:
            lines.append(f"  OK   resolve URL for {file_path}")

    if ok:
        lines.append("  OK   structural checks passed")
    return ok, lines


def verify_model_hf(model: dict, *, compute_missing: bool) -> tuple[bool, list[str]]:
    model_id = model.get("model_id", "<unknown>")
    lines: list[str] = [f"[{model_id}] hf"]
    ok = True

    for file_path in files_to_verify(model):
        expected = expected_hash(model, file_path)
        url = resolve_url(model, file_path)
        if not url:
            lines.append(f"  SKIP {file_path}: cannot build resolve URL (revision={model.get('revision')})")
            continue

        try:
            actual = fetch_hash(url)
        except urllib.error.URLError as exc:
            lines.append(f"  FAIL {file_path}: download error: {exc}")
            ok = False
            continue

        if expected:
            if actual == expected:
                lines.append(f"  OK   {file_path}: {actual}")
            else:
                lines.append(f"  FAIL {file_path}: expected {expected}, got {actual}")
                ok = False
        elif compute_missing:
            lines.append(f"  HASH {file_path}: {actual}")
        else:
            lines.append(f"  SKIP {file_path}: no expected hash in manifest")
            ok = False

    return ok, lines


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-id", action="append", default=[])
    parser.add_argument("--family", action="append", choices=sorted(FAMILY_MODELS.keys()), default=[])
    parser.add_argument(
        "--all-families",
        action="store_true",
        help="Deprecated alias for --all-audited.",
    )
    parser.add_argument(
        "--all-audited",
        action="store_true",
        help="Verify every commercial or hash-required model in the manifest.",
    )
    parser.add_argument(
        "--structural",
        action="store_true",
        help="Validate hash completeness and resolve URL buildability without HF downloads.",
    )
    parser.add_argument(
        "--verify-hf",
        action="store_true",
        help="Download artifacts from Hugging Face and compare SHA-256 digests.",
    )
    parser.add_argument(
        "--compute-missing",
        action="store_true",
        help="With --verify-hf, print SHA-256 for files that lack expected hashes.",
    )
    args = parser.parse_args()

    if not args.structural and not args.verify_hf:
        args.verify_hf = True

    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    model_ids: list[str] = list(args.model_id)
    if args.all_families or args.all_audited:
        model_ids.extend(audited_manifest_model_ids(catalog))
    for family in args.family:
        model_ids.extend(FAMILY_MODELS[family])

    if not model_ids:
        parser.error("Provide --model-id, --family, --all-audited, and/or deprecated --all-families")

    seen: set[str] = set()
    unique_model_ids: list[str] = []
    for model_id in model_ids:
        if model_id not in seen:
            seen.add(model_id)
            unique_model_ids.append(model_id)

    by_id = {m["model_id"]: m for m in catalog.get("models", [])}
    all_ok = True

    for model_id in unique_model_ids:
        model = by_id.get(model_id)
        if model is None:
            print(f"::error::Unknown model_id {model_id}", file=sys.stderr)
            all_ok = False
            continue

        if args.structural:
            ok, lines = verify_model_structural(model)
            all_ok = all_ok and ok
            print("\n".join(lines))
            print()

        if args.verify_hf:
            ok, lines = verify_model_hf(model, compute_missing=args.compute_missing)
            all_ok = all_ok and ok
            print("\n".join(lines))
            print()

    return 0 if all_ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
