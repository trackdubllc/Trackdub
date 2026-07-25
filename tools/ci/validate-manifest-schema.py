#!/usr/bin/env python3
"""Validate bundled-models.manifest.json against schema rules and profile registry."""

from __future__ import annotations

import json
import sys
from pathlib import Path

from manifest_schema_common import (
    ALLOWED_HASH_MODES,
    ALLOWED_TIERS,
    ALLOWED_VARIANT_PROVIDER_TOKENS,
    hash_required_paths,
    is_valid_sha256,
    normalized_provider_token,
    normalize_relative_path,
)

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json"
SCHEMA_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.schema.json"
PROFILES_PATH = REPO_ROOT / "src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.profiles.json"


def load_json(path: Path) -> dict:
    with path.open(encoding="utf-8-sig") as handle:
        return json.load(handle)


def validate_profiles(profiles: dict) -> list[str]:
    errors: list[str] = []
    for section in ("capabilities", "language_coverage"):
        if section not in profiles:
            errors.append(f"profiles missing '{section}' object.")
            continue
        if not isinstance(profiles[section], dict):
            errors.append(f"profiles.{section} must be an object.")
    return errors


def validate_model(model: dict, profiles: dict, *, index: int) -> list[str]:
    errors: list[str] = []
    model_id = str(model.get("model_id", f"<index {index}>"))
    prefix = f"[{model_id}]"

    required_strings = ("model_id", "task", "engine_family", "license")
    for field in required_strings:
        if not str(model.get(field, "")).strip():
            errors.append(f"{prefix} required field '{field}' is empty.")

    tier = str(model.get("tier") or "balanced")
    if tier not in ALLOWED_TIERS:
        errors.append(f"{prefix} tier '{tier}' is not one of {sorted(ALLOWED_TIERS)}.")

    if model.get("voice_cloning") is True and model.get("requires_user_consent") is not True:
        errors.append(f"{prefix} voice_cloning=true requires requires_user_consent=true.")

    if model.get("commercial_allowed") is True and str(model.get("license", "")).lower() in ("", "unknown"):
        errors.append(f"{prefix} commercial_allowed=true with unknown/empty license.")

    hash_mode = str((model.get("hash_verification") or {}).get("mode", "none")).lower()
    if hash_mode not in ALLOWED_HASH_MODES:
        errors.append(f"{prefix} hash_verification.mode '{hash_mode}' is invalid.")

    errors.extend(validate_variant_providers(model, prefix))

    if model.get("commercial_use_verified") is True:
        sha = str(model.get("sha256", "")).strip()
        benchmark = model.get("benchmark_entry")
        hashes = model.get("download_file_hashes") or {}
        if not sha and not (benchmark and benchmark in hashes):
            errors.append(f"{prefix} commercial_use_verified=true requires sha256 or benchmark hash evidence.")

    if hash_mode == "required":
        hashes = model.get("download_file_hashes") or {}
        if not hashes:
            errors.append(f"{prefix} hash_verification.mode=required but download_file_hashes is empty.")
        for rel_path, digest in hashes.items():
            try:
                normalize_relative_path(rel_path)
            except ValueError:
                errors.append(f"{prefix} unsafe download_file_hashes key '{rel_path}'.")
            if not is_valid_sha256(str(digest)):
                errors.append(f"{prefix} invalid SHA-256 for '{rel_path}'.")

        for required_path in hash_required_paths(model):
            if required_path not in {normalize_relative_path(k) for k in hashes}:
                errors.append(
                    f"{prefix} hash_verification.mode=required missing download_file_hashes['{required_path}']."
                )

        benchmark = model.get("benchmark_entry")
        top_sha = str(model.get("sha256", "")).strip()
        if benchmark and top_sha and benchmark in hashes:
            if str(hashes[benchmark]).lower() != top_sha.lower():
                errors.append(
                    f"{prefix} sha256 does not match download_file_hashes['{benchmark}']."
                )

    profile_ref = model.get("profile_ref")
    if profile_ref:
        caps_profiles = (profiles.get("capabilities") or {})
        lang_profiles = (profiles.get("language_coverage") or {})
        if profile_ref not in caps_profiles and profile_ref not in lang_profiles:
            errors.append(f"{prefix} profile_ref '{profile_ref}' not found in bundled-models.profiles.json.")

    return errors


def validate_variant_providers(model: dict, prefix: str) -> list[str]:
    errors: list[str] = []
    olive = ((model.get("optimization") or {}).get("olive") or {})
    olive_providers = {
        normalized_provider_token(str(provider))
        for provider in (olive.get("supported_providers") or [])
        if str(provider).strip()
    }
    fallback_policy = str(olive.get("fallback_policy", "")).strip().lower()
    if fallback_policy == "cpu_runtime_allowed":
        olive_providers.add("cpu")

    for variant_index, variant in enumerate(model.get("variants") or []):
        if not isinstance(variant, dict):
            errors.append(f"{prefix} variants[{variant_index}] must be an object.")
            continue

        raw_supported = variant.get("supported_providers")
        if raw_supported is None:
            continue
        if not isinstance(raw_supported, list):
            errors.append(f"{prefix} variants[{variant_index}].supported_providers must be an array.")
            continue

        seen: set[str] = set()
        normalized_supported: set[str] = set()
        for provider_index, provider in enumerate(raw_supported):
            if not isinstance(provider, str):
                errors.append(
                    f"{prefix} variants[{variant_index}].supported_providers[{provider_index}] must be a string."
                )
                continue
            token = provider.strip().lower()
            if token not in ALLOWED_VARIANT_PROVIDER_TOKENS:
                errors.append(
                    f"{prefix} variants[{variant_index}].supported_providers[{provider_index}] '{provider}' is invalid."
                )
                continue
            if token in seen:
                errors.append(f"{prefix} variants[{variant_index}].supported_providers duplicates '{provider}'.")
            seen.add(token)
            normalized_supported.add(normalized_provider_token(token))

        if olive_providers:
            unsupported = sorted(normalized_supported - olive_providers)
            if unsupported:
                alias = str(variant.get("alias", f"<index {variant_index}>"))
                errors.append(
                    f"{prefix} variant '{alias}' supported_providers {unsupported} not covered by Olive supported_providers {sorted(olive_providers)}."
                )

    return errors


def main() -> int:
    if not MANIFEST_PATH.is_file():
        print(f"::error::Manifest not found at {MANIFEST_PATH}", file=sys.stderr)
        return 1
    if not SCHEMA_PATH.is_file():
        print(f"::error::Schema not found at {SCHEMA_PATH}", file=sys.stderr)
        return 1
    if not PROFILES_PATH.is_file():
        print(f"::error::Profiles not found at {PROFILES_PATH}", file=sys.stderr)
        return 1

    catalog = load_json(MANIFEST_PATH)
    profiles = load_json(PROFILES_PATH)
    schema = load_json(SCHEMA_PATH)

    errors = validate_profiles(profiles)
    models = catalog.get("models")
    if not isinstance(models, list) or not models:
        errors.append("manifest.models must be a non-empty array.")
    else:
        for index, model in enumerate(models):
            if not isinstance(model, dict):
                errors.append(f"$.models[{index}] must be an object.")
                continue
            errors.extend(validate_model(model, profiles, index=index))

    print(f"Schema document: {schema.get('title', SCHEMA_PATH.name)}")
    print(f"Validated {len(models or [])} model entries against schema rules and profiles registry.")

    if errors:
        print(f"\nErrors: {len(errors)}")
        for error in errors:
            print(f"  ERROR: {error}")
        return 1

    print("Schema validation passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
