"""
Build whisper-{base,small,medium,large-v3} in onnxruntime-genai format
(fp32 CPU) and upload to tonythethompson/whisper-{size}-genai on HuggingFace.

Uses onnxruntime-genai's built-in model builder
(python -m onnxruntime_genai.models.builder).

Usage:
    uv run build-upload-whisper-genai.py base
    uv run build-upload-whisper-genai.py small
    uv run build-upload-whisper-genai.py medium
    uv run build-upload-whisper-genai.py large-v3

Or from the repo root with the conversion venv:
    D:/Dev/Trackdub/tools/dev/.venv-conversion/Scripts/python.exe \\
        tools/dev/model-conversion/build-upload-whisper-genai.py base
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

import sys
import shutil
import tempfile
from pathlib import Path
from huggingface_hub import HfApi  # type: ignore[import]
from onnxruntime_genai.models.builder import (  # type: ignore[import]
    create_model,
)

# Files the model builder emits at the output root (external-data format)
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

# openai/whisper-{size} -> model params for reference in README
MODEL_PARAMS = {
    "base":    "74M",
    "small":   "244M",
    "medium":  "769M",
    "large-v3": "1.5B",
}


def readme(size: str, hf_src: str, hf_dst: str) -> str:
    params = MODEL_PARAMS.get(size, "?")
    return f"""\
# {hf_dst}

`{hf_src}` exported to
[onnxruntime-genai](https://github.com/microsoft/onnxruntime-genai) format
(encoder + decoder ONNX with external data files, `genai_config.json`).

Built with the ort-genai model builder (fp32, CPU EP) for use with
`onnxruntime-genai` on CPU/DirectML/CUDA.

## Model

- Source: [{hf_src}](https://huggingface.co/{hf_src})
- Parameters: {params}
- Precision: fp32
- Format: onnxruntime-genai (encoder/decoder split with external data)

## Files

| File | Description |
|---|---|
| `encoder.onnx` + `encoder.onnx.data` | Encoder model |
| `decoder.onnx` + `decoder.onnx.data` | Decoder model |
| `genai_config.json` | onnxruntime-genai session config |
| `audio_processor_config.json` | Audio feature extractor config |
| `tokenizer.json` + `tokenizer_config.json` | Tokenizer |

## License

Apache-2.0 (same as [{hf_src}](https://huggingface.co/{hf_src}))

## Source

Derived from [{hf_src}](https://huggingface.co/{hf_src}).
Converted using the [onnxruntime-genai model builder](
https://github.com/microsoft/onnxruntime-genai).
"""


def build_and_upload(size: str):
    hf_src = f"openai/whisper-{size}"
    hf_dst = f"tonythethompson/whisper-{size}-genai"
    print(f"\n=== Building {hf_src} -> {hf_dst} ===\n")

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        output_dir = tmp_path / "output"
        output_dir.mkdir()
        cache_dir = tmp_path / "cache"
        cache_dir.mkdir()

        # Run model builder via direct import (avoids subprocess path issues)
        print("Running onnxruntime-genai model builder (fp32 / cpu) ...")
        create_model(
            model_name=hf_src,
            input_path="",
            output_dir=str(output_dir),
            precision="fp32",
            execution_provider="cpu",
            cache_dir=str(cache_dir),
        )
        print("\nModel builder complete.")

        # List what was produced
        produced = sorted(
            str(p.relative_to(output_dir))
            for p in output_dir.rglob("*")
            if p.is_file()
        )
        print("Produced files:")
        for f in produced:
            size_mb = (output_dir / f).stat().st_size / 1024 / 1024
            print(f"  {f}  ({size_mb:.1f} MB)")

        # Check all expected upload files exist
        missing = [f for f in UPLOAD_FILES if not (output_dir / f).exists()]
        if missing:
            raise FileNotFoundError(
                f"Model builder did not produce expected files: {missing}\n"
                f"Got: {produced}"
            )

        # Stage upload (copy only the files we want)
        staging = tmp_path / "staging"
        staging.mkdir()
        for fname in UPLOAD_FILES:
            src = output_dir / fname
            dst = staging / fname
            mb = src.stat().st_size / 1024 / 1024
            print(f"  Staging {fname}  ({mb:.1f} MB)")
            shutil.copy2(src, dst)

        (staging / "README.md").write_text(
            readme(size, hf_src, hf_dst), encoding="utf-8"
        )

        print(f"\nUploading to {hf_dst} ...")
        api = HfApi()
        commit = api.upload_folder(
            folder_path=str(staging),
            repo_id=hf_dst,
            repo_type="model",
            commit_message=f"Add onnxruntime-genai fp32 export of {hf_src}",
        )
        print(f"\nUploaded! Commit URL: {commit}")

        repo_info = api.repo_info(repo_id=hf_dst, repo_type="model")
        sha = repo_info.sha
        print(f"Commit SHA: {sha}")
        print(f"\nManifest entry for whisper-{size}-genai:")
        print(f'  "source_url": "https://huggingface.co/{hf_dst}",')
        print(f'  "revision": "{sha}",')
        return sha


if __name__ == "__main__":
    _size = sys.argv[1] if len(sys.argv) > 1 else "base"
    valid = {"base", "small", "medium", "large-v3"}
    if _size not in valid:
        print(f"Unknown size '{_size}'. Choose from: {sorted(valid)}")
        sys.exit(1)
    try:
        build_and_upload(_size)
    except KeyboardInterrupt:
        print("\nAborted by user.")
        sys.exit(130)
    except Exception as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        sys.exit(1)
