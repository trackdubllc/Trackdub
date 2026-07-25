#!/usr/bin/env python3
"""Fetch SHA-256 for explicit Hugging Face resolve paths."""

from __future__ import annotations

import argparse
import hashlib
import json
import urllib.request


def sha256_url(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "Trackdub-manifest-hash-audit/1.0"})
    with urllib.request.urlopen(request, timeout=300) as response:
        return hashlib.sha256(response.read()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", action="append", required=True)
    args = parser.parse_args()

    results: dict[str, str] = {}
    for url in args.url:
        path = url.split("/resolve/")[-1].split("/", 1)[-1]
        digest = sha256_url(url)
        results[path] = digest
        print(f"{path}: {digest}")

    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
