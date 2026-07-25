"""
Convert Helsinki-NLP/opus-mt-tc-big-en-pt to ONNX (merged-decoder variant)
and upload to tonythethompson/opus-mt-en-pt on HuggingFace.

Source: Helsinki-NLP/opus-mt-tc-big-en-pt (Helsinki OPUS-MT TC-Big, en->pt)
Target: tonythethompson/opus-mt-en-pt

Requires: optimum[onnxruntime] transformers huggingface_hub torch sentencepiece

Usage:
    python convert-tc-big-en-pt.py
"""

# /// script
# requires-python = ">=3.10"
# dependencies = [
#   "optimum[onnxruntime]",
#   "transformers",
#   "torch",
#   "huggingface_hub",
#   "sentencepiece",
# ]
# ///

import shutil
import tempfile
from pathlib import Path
from optimum.exporters.onnx import main_export  # type: ignore[import]
from huggingface_hub import HfApi  # type: ignore[import]

SRC_MODEL = "Helsinki-NLP/opus-mt-tc-big-en-pt"
HF_REPO = "tonythethompson/opus-mt-en-pt"


def convert_and_upload():
    print(f"\n=== Converting {SRC_MODEL} -> {HF_REPO} ===\n")

    with tempfile.TemporaryDirectory() as tmp:
        out_dir = Path(tmp) / "export"
        out_dir.mkdir()

        print(f"Exporting ONNX to {out_dir} ...")
        main_export(
            model_name_or_path=SRC_MODEL,
            output=out_dir,
            task="seq2seq-lm-with-past",
            framework="pt",
            opset=17,
            no_post_process=False,
        )

        # List what was exported
        exported = sorted(
            str(p.relative_to(out_dir))
            for p in out_dir.rglob("*")
            if p.is_file()
        )
        print("Exported files:", exported)

        # Verify merged decoder and encoder exist
        merged = out_dir / "decoder_model_merged.onnx"
        encoder = out_dir / "encoder_model.onnx"
        if not merged.exists():
            raise FileNotFoundError(
                f"Expected decoder_model_merged.onnx, got: {exported}"
            )
        if not encoder.exists():
            raise FileNotFoundError(
                f"Expected encoder_model.onnx, got: {exported}"
            )

        # Add a README
        readme = out_dir / "README.md"
        readme.write_text(
            f"# {HF_REPO}\n\n"
            f"ONNX export of [{SRC_MODEL}]"
            f"(https://huggingface.co/{SRC_MODEL}) "
            f"for use with [onnxruntime](https://onnxruntime.ai/).\n\n"
            "Generated with [Trackdub]"
            "(https://github.com/Babelworks/Trackdub)"
            " model conversion tooling.\n\n"
            "## Files\n"
            "- `onnx/encoder_model.onnx` — encoder\n"
            "- `onnx/decoder_model_merged.onnx` — merged decoder"
            " (with and without past KV cache)\n"
            "- `source.spm`, `target.spm`, `vocab.json`"
            " — tokenizer files\n\n"
            f"## Source\nConverted from [{SRC_MODEL}]"
            f"(https://huggingface.co/{SRC_MODEL})\n\n"
            "## License\nCC-BY-4.0 (inherited from source)\n"
        )

        # Reorganise: move onnx files into onnx/ subdir
        onnx_subdir = out_dir / "onnx"
        onnx_subdir.mkdir(exist_ok=True)
        for f in list(out_dir.glob("*.onnx")):
            shutil.move(str(f), str(onnx_subdir / f.name))

        print("\nFinal layout:")
        for p in sorted(out_dir.rglob("*")):
            if p.is_file():
                size_mb = p.stat().st_size / 1024 / 1024
                print(f"  {p.relative_to(out_dir)}  ({size_mb:.1f} MB)")

        print(f"\nUploading to {HF_REPO} ...")
        api = HfApi()
        commit = api.upload_folder(
            folder_path=str(out_dir),
            repo_id=HF_REPO,
            repo_type="model",
            commit_message=f"Add ONNX export of {SRC_MODEL}",
        )
        print(f"\nUploaded! Commit URL: {commit}")

        # Get the commit SHA
        repo_info = api.repo_info(repo_id=HF_REPO, repo_type="model")
        sha = repo_info.sha
        print(f"Commit SHA: {sha}")
        print(f"\nManifest revision: {sha}")


if __name__ == "__main__":
    import sys  # noqa: PLC0415 — only needed at entry point
    try:
        convert_and_upload()
    except KeyboardInterrupt:
        print("\nAborted by user.")
        sys.exit(130)
    except Exception as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        sys.exit(1)
