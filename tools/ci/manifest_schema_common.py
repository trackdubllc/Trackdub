"""Shared bundled manifest validation helpers for CI scripts."""

from __future__ import annotations

import re
from typing import Iterable

SHA256_RE = re.compile(r"^[0-9a-fA-F]{64}$")
ALLOWED_TIERS = frozenset({"fast", "balanced", "quality", "accurate"})
ALLOWED_HASH_MODES = frozenset({"none", "required", "verify-if-sha-present"})
ALLOWED_VARIANT_PROVIDER_TOKENS = frozenset(
    {"cpu", "dml", "directml", "cuda", "tensorrt", "trt-rtx", "tensorrt-rtx", "migraphx", "rocm"}
)

_PROVIDER_ALIASES = {
    "directml": "dml",
    "dml": "dml",
    "tensorrt-rtx": "trt-rtx",
    "trt-rtx": "trt-rtx",
    "rocm": "migraphx",
    "migraphx": "migraphx",
}


def is_valid_sha256(value: str) -> bool:
    return bool(value and SHA256_RE.fullmatch(value))


def normalize_relative_path(path: str) -> str:
    normalized = path.replace("\\", "/").strip("/")
    parts = normalized.split("/")
    if not normalized or any(part in ("", ".", "..") for part in parts):
        raise ValueError(f"unsafe relative path: {path}")
    return normalized


def hash_required_paths(model: dict) -> list[str]:
    paths: list[str] = []
    seen: set[str] = set()

    def add(path: str | None) -> None:
        if not path:
            return
        normalized = normalize_relative_path(path)
        if normalized not in seen:
            seen.add(normalized)
            paths.append(normalized)

    for download_file in model.get("download_files") or []:
        add(download_file)

    for variant in model.get("variants") or []:
        alias = str(variant.get("alias", ""))
        if variant.get("is_default") or alias.lower() == "default":
            add(variant.get("entry_path"))
            for download_file in variant.get("download_files") or []:
                add(download_file)

    add(model.get("benchmark_entry"))
    return paths


def resolve_url(model: dict, file_path: str) -> str | None:
    sources = model.get("download_file_sources") or {}
    if file_path in sources:
        return sources[file_path]

    revision = model.get("revision")
    source_url = str(model.get("source_url", "")).strip()
    if not revision or revision == "cache-installed" or not source_url:
        return None

    return f"{source_url.rstrip('/')}/resolve/{revision}/{file_path}"


def iter_audited_family_model_ids(family_models: dict[str, list[str]]) -> list[str]:
    ids: list[str] = []
    for family in sorted(family_models):
        ids.extend(family_models[family])
    return ids


def normalized_provider_token(token: str) -> str:
    normalized = token.strip().lower()
    return _PROVIDER_ALIASES.get(normalized, normalized)


def audited_manifest_model_ids(catalog: dict) -> list[str]:
    ids: list[str] = []
    for model in catalog.get("models", []):
        if not isinstance(model, dict):
            continue
        hash_mode = str((model.get("hash_verification") or {}).get("mode", "none")).lower()
        if model.get("commercial_use_verified") is True or hash_mode == "required":
            model_id = str(model.get("model_id", "")).strip()
            if model_id:
                ids.append(model_id)
    return ids


def commercial_verified_models(catalog: dict) -> list[dict]:
    return [model for model in catalog.get("models", []) if model.get("commercial_use_verified") is True]
