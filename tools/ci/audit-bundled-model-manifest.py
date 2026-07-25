#!/usr/bin/env python3
"""Validate bundled-models.manifest.json for CI (model-audit workflow)."""

from __future__ import annotations

import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
NOTICES_PATH = REPO_ROOT / "THIRD_PARTY_NOTICES.md"
SUMMARY_PATH = REPO_ROOT / "audit-summary.md"


def main() -> int:
    if not MANIFEST_PATH.is_file():
        print(f"::error::Manifest not found at {MANIFEST_PATH}", file=sys.stderr)
        return 1

    with MANIFEST_PATH.open(encoding="utf-8-sig") as f:
        data = json.load(f)

    models = data.get("models", [])
    print(f"Total models in manifest: {len(models)}")

    errors: list[str] = []
    warnings: list[str] = []

    try:
        notices_content = NOTICES_PATH.read_text(encoding="utf-8")
    except FileNotFoundError:
        notices_content = ""
        warnings.append("THIRD_PARTY_NOTICES.md not found — cannot verify attribution.")

    for i, model in enumerate(models):
        mid = model.get("model_id", f"<index {i}>")
        prefix = f"[{mid}]"

        if model.get("commercial_use_verified") is True:
            sha = model.get("sha256", "")
            if not sha or not str(sha).strip():
                errors.append(f"{prefix} commercial_use_verified=true but sha256 is empty.")

        if model.get("commercial_allowed") is True:
            if str(model.get("license", "")).lower() in ("unknown", ""):
                errors.append(
                    f"{prefix} commercial_allowed=true but license is '{model.get('license')}'."
                )

        if model.get("voice_cloning") is True:
            if model.get("requires_user_consent") is not True:
                errors.append(f"{prefix} voice_cloning=true but requires_user_consent is not true.")

        if not str(model.get("source_url", "")).strip():
            warnings.append(f"{prefix} source_url is empty.")

        if model.get("requires_attribution") is True:
            engine_family = model.get("engine_family", "")
            if mid not in notices_content and engine_family not in notices_content:
                warnings.append(
                    f"{prefix} requires_attribution=true but not found in THIRD_PARTY_NOTICES.md."
                )

        for field in ("model_id", "task", "license", "engine_family"):
            if not str(model.get(field, "")).strip():
                errors.append(f"{prefix} required field '{field}' is empty.")

    print(f"\nErrors: {len(errors)}")
    for error in errors:
        print(f"  ERROR: {error}")

    print(f"\nWarnings: {len(warnings)}")
    for warning in warnings:
        print(f"  WARN: {warning}")

    with SUMMARY_PATH.open("w", encoding="utf-8") as f:
        f.write("## Model Manifest Audit\n\n")
        f.write(f"**{len(models)}** models audited.\n\n")
        if errors:
            f.write(f"### Errors ({len(errors)})\n\n")
            for error in errors:
                f.write(f"- {error}\n")
            f.write("\n")
        if warnings:
            f.write(f"### Warnings ({len(warnings)})\n\n")
            for warning in warnings:
                f.write(f"- {warning}\n")
            f.write("\n")
        if not errors and not warnings:
            f.write("All checks passed.\n")

    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
