"""
Build Qwen/Qwen2.5-1.5B-Instruct in onnxruntime-genai format (fp32 CPU)
and upload to tonythethompson/Qwen2.5-1.5B-Instruct on Hugging Face.

Usage:
    uv run tools/dev/model-conversion/build-upload-qwen-text-refinement-genai.py
    uv run tools/dev/model-conversion/build-upload-qwen-text-refinement-genai.py --dry-run
    uv run tools/dev/model-conversion/build-upload-qwen-text-refinement-genai.py --local-dir models/Qwen2.5-1.5B-Instruct
"""

# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "onnxruntime-genai>=0.13.2",
#   "onnx-ir>=0.2.1",
#   "onnx>=1.21.0",
#   "huggingface_hub",
#   "transformers",
#   "torch",
# ]
# ///

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import tempfile
from pathlib import Path

from huggingface_hub import HfApi, hf_hub_download  # type: ignore[import]
from onnxruntime_genai.models.builder import create_model  # type: ignore[import]

HF_SRC = "Qwen/Qwen2.5-1.5B-Instruct"
HF_DST = "tonythethompson/Qwen2.5-1.5B-Instruct"

UPLOAD_FILES = [
    "genai_config.json",
    "model.onnx",
    "model.onnx.data",
    "tokenizer.json",
    "tokenizer_config.json",
    "config.json",
    "chat_template.jinja",
]

SUPPLEMENT_FROM_SOURCE = [
    "config.json",
]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def readme() -> str:
    return f"""\
# {HF_DST}

`{HF_SRC}` exported to
[onnxruntime-genai](https://github.com/microsoft/onnxruntime-genai) format
(`genai_config.json` + `model.onnx` external data) for Trackdub ASR text polish.

Built with the ort-genai model builder (fp32, CPU EP) for use with
`onnxruntime-genai` on CPU/DirectML/CUDA.

## Model

- Source: [{HF_SRC}](https://huggingface.co/{HF_SRC})
- Parameters: 1.5B
- Precision: fp32
- Format: onnxruntime-genai

## Files

| File | Description |
|---|---|
| `model.onnx` + `model.onnx.data` | GenAI model |
| `genai_config.json` | onnxruntime-genai session config |
| `tokenizer.json` + `tokenizer_config.json` | Tokenizer |
| `config.json` | Model config (from upstream HF) |
| `chat_template.jinja` | Chat template |

## License

Apache-2.0 (same as [{HF_SRC}](https://huggingface.co/{HF_SRC}))

## Source

Derived from [{HF_SRC}](https://huggingface.co/{HF_SRC}).
Converted using the [onnxruntime-genai model builder](
https://github.com/microsoft/onnxruntime-genai).
"""


def build(output_dir: Path, cache_dir: Path) -> list[str]:
    print(f"Running onnxruntime-genai model builder for {HF_SRC} (fp32 / cpu) ...")
    create_model(
        model_name=HF_SRC,
        input_path="",
        output_dir=str(output_dir),
        precision="fp32",
        execution_provider="cpu",
        cache_dir=str(cache_dir),
    )

    produced = sorted(
        str(path.relative_to(output_dir).as_posix())
        for path in output_dir.rglob("*")
        if path.is_file()
    )
    print("Produced files:")
    for relative in produced:
        size_mb = (output_dir / relative).stat().st_size / 1024 / 1024
        print(f"  {relative}  ({size_mb:.1f} MB)")

    supplement_from_source(output_dir, cache_dir)

    missing = [name for name in UPLOAD_FILES if not (output_dir / name).exists()]
    if missing:
        raise FileNotFoundError(
            f"Model builder did not produce expected files: {missing}\nGot: {produced}"
        )

    return produced


def supplement_from_source(output_dir: Path, cache_dir: Path) -> None:
    for name in SUPPLEMENT_FROM_SOURCE:
        if (output_dir / name).exists():
            continue

        print(f"Fetching missing {name} from {HF_SRC} ...")
        downloaded = hf_hub_download(
            repo_id=HF_SRC,
            filename=name,
            cache_dir=str(cache_dir),
        )
        shutil.copy2(downloaded, output_dir / name)


def stage_upload_files(output_dir: Path, staging_dir: Path) -> None:
    staging_dir.mkdir(parents=True, exist_ok=True)
    for name in UPLOAD_FILES:
        src = output_dir / name
        dst = staging_dir / name
        size_mb = src.stat().st_size / 1024 / 1024
        print(f"  Staging {name}  ({size_mb:.1f} MB)")
        shutil.copy2(src, dst)

    (staging_dir / "README.md").write_text(readme(), encoding="utf-8")


def print_manifest_snippet(revision: str, staging_dir: Path) -> None:
    hashes = {name: sha256_file(staging_dir / name) for name in UPLOAD_FILES}
    benchmark_hash = hashes["genai_config.json"]
    sources = {
        name: f"https://huggingface.co/{HF_DST}/resolve/{revision}/{name}"
        for name in UPLOAD_FILES
    }

    print("\nManifest patch values:")
    print(f'  "revision": "{revision}",')
    print(f'  "sha256": "{benchmark_hash}",')
    print('  "download_file_hashes":')
    print(json.dumps(hashes, indent=4))
    print('  "download_file_sources":')
    print(json.dumps(sources, indent=4))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true", help="Build only; do not upload.")
    parser.add_argument(
        "--local-dir",
        type=Path,
        help="Copy built bundle here after staging (e.g. models/Qwen2.5-1.5B-Instruct).",
    )
    args = parser.parse_args()

    try:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            output_dir = tmp_path / "output"
            output_dir.mkdir()
            cache_dir = tmp_path / "cache"
            cache_dir.mkdir()

            build(output_dir, cache_dir)

            staging = tmp_path / "staging"
            print("\nStaging upload files ...")
            stage_upload_files(output_dir, staging)

            if args.local_dir is not None:
                args.local_dir.mkdir(parents=True, exist_ok=True)
                for name in [*UPLOAD_FILES, "README.md"]:
                    shutil.copy2(staging / name, args.local_dir / name)
                print(f"\nCopied bundle to {args.local_dir.resolve()}")

            if args.dry_run:
                print("\nDry run complete (no upload).")
                print_manifest_snippet("DRY_RUN_REVISION", staging)
                return 0

            print(f"\nUploading to {HF_DST} ...")
            api = HfApi()
            commit = api.upload_folder(
                folder_path=str(staging),
                repo_id=HF_DST,
                repo_type="model",
                commit_message=f"Add onnxruntime-genai fp32 export of {HF_SRC} for text refinement",
            )
            print(f"\nUploaded! Commit URL: {commit}")

            repo_info = api.repo_info(repo_id=HF_DST, repo_type="model")
            revision = repo_info.sha
            print(f"Commit SHA: {revision}")
            print_manifest_snippet(revision, staging)
            return 0
    except KeyboardInterrupt:
        print("\nAborted by user.")
        return 130
    except Exception as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
