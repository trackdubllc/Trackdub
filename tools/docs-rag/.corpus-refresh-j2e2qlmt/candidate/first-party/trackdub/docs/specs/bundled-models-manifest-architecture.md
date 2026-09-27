# Bundled manifest schema and profiles

Architecture for `bundled-models.manifest.json` validation, deduplication, and future loader support.

## Files

| File | Role |
|------|------|
| `bundled-models.manifest.json` | Shipping inventory (40 bundled ONNX models) |
| `bundled-models.manifest.schema.json` | JSON Schema contract for manifest entries |
| `bundled-models.profiles.json` | Reusable capability and language-coverage profiles |
| `tools/ci/validate-manifest-schema.py` | CI structural validator (stdlib; mirrors loader rules) |
| `tools/ci/verify-manifest-hashes.py` | HF SHA-256 verification for audited families |
| `tools/ci/audit-bundled-model-manifest.py` | License/commercial gate checks |

## Profiles (phase 1)

`bundled-models.profiles.json` holds canonical lists today used as **reference data**:

- `capabilities.text-refinement-standard`
- `capabilities.translation-direct`
- `capabilities.asr-whisper-auto`
- `language_coverage.whisper-source-auto` (`["auto"]`)
- `language_coverage.qwen3-asr-multilingual`
- `language_coverage.nemotron-asr-multilingual`

Manifest entries still inline their `capabilities` / `language_coverage` values. CI does not require `profile_ref` yet.

## Profiles (phase 2 — planned)

1. Add optional `profile_ref` on manifest entries.
2. Teach `ModelManifestLoader` to expand `profile_ref` into capabilities/language coverage at load time.
3. Migrate duplicated Qwen3/Nemotron language lists to profile references in the manifest.
4. Keep `ModelManifestTests` coverage for expanded catalog shape.

## Schema validation

`validate-manifest-schema.py` enforces:

- Required model fields and allowed `tier` values (`fast`, `balanced`, `quality`, `accurate`)
- `commercial_use_verified` hash evidence (`sha256` or benchmark entry hash)
- `hash_verification.mode=required` completeness for `download_files`, default variant paths, and `benchmark_entry`
- `sha256` alignment with `download_file_hashes[benchmark_entry]` when both are present
- Optional `profile_ref` keys must exist in `bundled-models.profiles.json`

`bundled-models.manifest.schema.json` is the machine-readable contract; the Python validator is the CI gate (no extra Python deps).

## Hash verification CI

| Trigger | Steps |
|---------|-------|
| Pull request | `audit-bundled-model-manifest.py` + `validate-manifest-schema.py` + `verify-manifest-hashes.py --structural --all-families` |
| Weekly cron / manual | Above + `verify-manifest-hashes.py --verify-hf --all-families` (HF resolve downloads) |

Structural mode checks hash completeness and resolve URL buildability without downloading artifacts. HF mode re-verifies pinned digests against Hugging Face.

`cache-installed` revisions (for example sortformer) are skipped in HF mode with explicit warnings; they rely on local cache layout and separate smoke tests.

## Authoring workflow

```powershell
python tools/ci/compute-family-hashes.py --model-id onnx-community/opus-mt-en-es
python tools/ci/verify-manifest-hashes.py --family opus-mt-onnx-community --structural
python tools/ci/verify-manifest-hashes.py --family opus-mt-onnx-community --verify-hf
python tools/ci/validate-manifest-schema.py
python tools/ci/audit-bundled-model-manifest.py
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~ModelManifest" -m:1
```

Apply scripts (`apply-wave*-commercial-audit.py`) must stay aligned with live manifest layout. Re-running an outdated apply script can regress `download_files` (for example dropping Opus merged-decoder ONNX or Qwen GenAI files).
