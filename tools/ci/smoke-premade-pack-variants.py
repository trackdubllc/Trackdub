#!/usr/bin/env python3
"""Smoke-check Trackdub premade HF variant mirror URLs from bundled manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
MIRROR_PREFIX = "https://huggingface.co/tonythethompson/trackdub-"
QUICK_SENTINEL_SUFFIXES = (
    "config.json",
    "genai_config.json",
    "tokenizer_config.json",
    "added_tokens.json",
    "special_tokens_map.json",
)


def quick_probe_priority(relative_path: str) -> tuple[int, int, str]:
    """Prefer small JSON sentinel files over large ONNX weights in --quick mode."""
    normalized = relative_path.replace("\\", "/").lower()
    for index, suffix in enumerate(QUICK_SENTINEL_SUFFIXES):
        if normalized.endswith(suffix):
            return (0, index, relative_path)
    if normalized.endswith(".json"):
        return (1, 0, relative_path)
    return (2, 0, relative_path)


def is_large_binary_path(relative_path: str) -> bool:
    normalized = relative_path.replace("\\", "/").lower()
    return normalized.endswith((".onnx", ".onnx.data", ".bin", ".safetensors"))


def sha256_url(url: str, timeout: int = 600) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "Trackdub-premade-variant-smoke/1.0"})
    digest = hashlib.sha256()
    with urllib.request.urlopen(request, timeout=timeout) as response:
        while True:
            chunk = response.read(8 * 1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def quick_reachability_probe(url: str, relative_path: str, timeout: int = 60) -> None:
    """Verify mirror URL without downloading large weights (HEAD, then 1-byte range)."""
    headers = {"User-Agent": "Trackdub-premade-variant-smoke/1.0"}
    head_request = urllib.request.Request(url, method="HEAD", headers=headers)
    try:
        with urllib.request.urlopen(head_request, timeout=timeout) as response:
            if response.status >= 400:
                raise urllib.error.URLError(f"HTTP {response.status}")
            return
    except urllib.error.HTTPError as exc:
        if exc.code not in (405, 501):
            raise
    except urllib.error.URLError:
        if not is_large_binary_path(relative_path):
            raise

    range_request = urllib.request.Request(
        url,
        headers={**headers, "Range": "bytes=0-0"},
    )
    with urllib.request.urlopen(range_request, timeout=timeout) as response:
        response.read(1)


def iter_mirror_sources(catalog: dict) -> list[tuple[str, str, str, str | None]]:
    rows: list[tuple[str, str, str, str | None]] = []
    for model in catalog.get("models", []):
        model_id = str(model.get("model_id", ""))
        sources = model.get("download_file_sources") or {}
        hashes = model.get("download_file_hashes") or {}
        for relative_path, url in sources.items():
            if not str(url).startswith(MIRROR_PREFIX):
                continue
            expected = hashes.get(relative_path)
            rows.append((model_id, relative_path, str(url), str(expected).lower() if expected else None))
    return sorted(rows, key=lambda row: (row[0], row[1]))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--quick",
        action="store_true",
        help="Reachability-check one URL per mirror repo (HEAD or 1-byte range; no full weight download).",
    )
    args = parser.parse_args()

    with MANIFEST_PATH.open(encoding="utf-8-sig") as handle:
        catalog = json.load(handle)

    rows = iter_mirror_sources(catalog)
    if not rows:
        print("No tonythethompson/trackdub-* download_file_sources found.", file=sys.stderr)
        return 1

    if args.quick:
        by_repo: dict[str, tuple[str, str, str, str | None]] = {}
        for row in rows:
            repo = row[2].split("/resolve/")[0]
            if repo not in by_repo or quick_probe_priority(row[1]) < quick_probe_priority(by_repo[repo][1]):
                by_repo[repo] = row
        rows = sorted(by_repo.values(), key=lambda row: row[2])

    ok = True
    for model_id, relative_path, url, expected in rows:
        label = f"[{model_id}] {relative_path}"
        try:
            if args.quick:
                quick_reachability_probe(url, relative_path)
                print(f"OK   {label}: reachability")
                continue

            actual = sha256_url(url)
        except urllib.error.URLError as exc:
            print(f"FAIL {label}: {exc}")
            ok = False
            continue

        if expected and actual != expected:
            print(f"FAIL {label}: expected {expected}, got {actual}")
            ok = False
        elif expected:
            print(f"OK   {label}: {actual}")
        else:
            print(f"OK   {label}: {actual} (no manifest hash)")

    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
