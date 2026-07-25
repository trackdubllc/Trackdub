"""
Upload the locally built whisper-tiny-genai model (onnxruntime-genai format)
to tonythethompson/whisper-tiny-genai on HuggingFace.

The root-level files (encoder.onnx + .data, decoder.onnx + .data, configs)
are uploaded. Olive-optimized subdirectories are excluded.

Usage:
    python upload-whisper-genai.py [path-to-model-root]
"""

# /// script
# requires-python = ">=3.10"
# dependencies = ["huggingface_hub"]
# ///

# pylint: disable=import-error,invalid-name
import sys
import shutil
import tempfile
from pathlib import Path
from huggingface_hub import HfApi  # type: ignore[import]

# Root-level files to upload (exclude Olive-optimized subdirs and benchmarks)
UPLOAD_FILES = [
    "audio_processor_config.json",
    "decoder.onnx",
    "decoder.onnx.data",
    "encoder.onnx",
    "encoder.onnx.data",
    "genai_config.json",
    "tokenizer.json",
    "tokenizer_config.json",
]

README = """\
# whisper-tiny-genai

`openai/whisper-tiny` exported to
[onnxruntime-genai](https://github.com/microsoft/onnxruntime-genai) format
(encoder + decoder ONNX with external data files, `genai_config.json`).

Built with the ort-genai model builder for use with `onnxruntime-genai`
on CPU/DirectML/CUDA.

## Files

| File | Description |
|---|---|
| `encoder.onnx` + `encoder.onnx.data` | Encoder model |
| `decoder.onnx` + `decoder.onnx.data` | Decoder model |
| `genai_config.json` | onnxruntime-genai session config |
| `audio_processor_config.json` | Audio feature extractor config |
| `tokenizer.json` + `tokenizer_config.json` | Tokenizer |

## License

Apache-2.0 (same as
[openai/whisper-tiny](https://huggingface.co/openai/whisper-tiny))

## Source

Derived from [openai/whisper-tiny](https://huggingface.co/openai/whisper-tiny).
Converted using the [onnxruntime-genai model builder](
https://github.com/microsoft/onnxruntime-genai).
"""


def upload(
    model_root: Path,
    hf_repo: str = "tonythethompson/whisper-tiny-genai",
):
    """Stage and upload whisper-tiny-genai model files to HuggingFace."""
    print(f"\n=== Uploading {model_root} -> {hf_repo} ===\n")

    missing = [f for f in UPLOAD_FILES if not (model_root / f).exists()]
    if missing:
        raise FileNotFoundError(f"Missing files in model root: {missing}")

    with tempfile.TemporaryDirectory() as tmp:
        staging = Path(tmp) / "upload"
        staging.mkdir()

        for fname in UPLOAD_FILES:
            src = model_root / fname
            dst = staging / fname
            size_mb = src.stat().st_size / 1024 / 1024
            print(f"  Staging {fname}  ({size_mb:.1f} MB)")
            shutil.copy2(src, dst)

        (staging / "README.md").write_text(README)

        print(f"\nTotal staged files: {len(list(staging.iterdir()))}")
        print(f"\nUploading to {hf_repo} ...")

        api = HfApi()
        commit = api.upload_folder(
            folder_path=str(staging),
            repo_id=hf_repo,
            repo_type="model",
            commit_message="Add onnxruntime-genai format whisper-tiny model",
        )
        print(f"\nUploaded! Commit URL: {commit}")

        repo_info = api.repo_info(repo_id=hf_repo, repo_type="model")
        sha = repo_info.sha
        print(f"Commit SHA: {sha}")
        print(f"\nManifest revision: {sha}")
        return sha


if __name__ == "__main__":
    _default = (
        Path(__file__).parent.parent.parent.parent
        / "models"
        / "whisper-tiny-genai"
    )
    _model_root = Path(sys.argv[1]) if len(sys.argv) > 1 else _default
    if not _model_root.exists():
        print(f"Model root not found: {_model_root}")
        sys.exit(1)
    try:
        upload(_model_root)
    except KeyboardInterrupt:
        print("\nAborted by user.")
        sys.exit(130)
    except Exception as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        sys.exit(1)
