"""
Convert Helsinki-NLP/opus-mt-{en-pt,es-pt} to ONNX (merged-decoder variant)
and upload to tonythethompson/{repo} on HuggingFace.

Requires: optimum[onnxruntime] transformers huggingface_hub torch

Usage:
    python convert-and-upload-opus.py en-pt
    python convert-and-upload-opus.py es-pt
"""

# /// script
# requires-python = ">=3.10"
# dependencies = [
#   "optimum[onnxruntime]",
#   "transformers",
#   "torch",
#   "huggingface_hub",
# ]
# ///

# pylint: disable=import-error,invalid-name
import sys
import shutil
import tempfile
from pathlib import Path
from optimum.exporters.onnx import main_export  # type: ignore[import]
from huggingface_hub import HfApi  # type: ignore[import]


def convert_and_upload(pair: str):
    """Convert an opus-mt model pair to ONNX and upload to HuggingFace."""
    src_model = f"Helsinki-NLP/opus-mt-{pair}"
    hf_repo = f"tonythethompson/opus-mt-{pair}"
    print(f"\n=== Converting {src_model} -> {hf_repo} ===\n")

    with tempfile.TemporaryDirectory() as tmp:
        out_dir = Path(tmp) / "onnx"
        out_dir.mkdir()

        print(f"Exporting ONNX to {out_dir} ...")
        main_export(
            model_name_or_path=src_model,
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

        # Verify merged decoder exists
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
            f"# {hf_repo}\n\n"
            f"ONNX export of [{src_model}]"
            f"(https://huggingface.co/{src_model}) "
            f"for use with [onnxruntime](https://onnxruntime.ai/).\n\n"
            "Generated with [Trackdub]"
            "(https://github.com/Babelworks/Trackdub)"
            " model conversion tooling.\n\n"
            f"## Files\n"
            f"- `onnx/encoder_model.onnx` — encoder\n"
            f"- `onnx/decoder_model_merged.onnx` — merged decoder"
            f" (with and without past KV cache)\n"
            "- `source.spm`, `target.spm`, `vocab.json`"
            " — tokenizer files\n\n"
            f"## License\nApache-2.0 (inherited from {src_model})\n"
        )

        # Reorganise: move onnx files into onnx/ subdir, keep tokenizer at root
        # optimum exports flat - move .onnx files into onnx/ subdir
        onnx_subdir = out_dir / "onnx"
        onnx_subdir.mkdir(exist_ok=True)
        for f in list(out_dir.glob("*.onnx")):
            shutil.move(str(f), str(onnx_subdir / f.name))

        print("\nFinal layout:")
        for p in sorted(out_dir.rglob("*")):
            if p.is_file():
                size_mb = p.stat().st_size / 1024 / 1024
                print(f"  {p.relative_to(out_dir)}  ({size_mb:.1f} MB)")

        print(f"\nUploading to {hf_repo} ...")
        api = HfApi()
        commit = api.upload_folder(
            folder_path=str(out_dir),
            repo_id=hf_repo,
            repo_type="model",
            commit_message=f"Add ONNX export of {src_model}",
        )
        print(f"\nUploaded! Commit URL: {commit}")

        # Get the commit SHA
        repo_info = api.repo_info(repo_id=hf_repo, repo_type="model")
        sha = repo_info.sha
        print(f"Commit SHA: {sha}")
        print(f"\nManifest revision for {pair}: {sha}")


if __name__ == "__main__":
    _pair = sys.argv[1] if len(sys.argv) > 1 else "en-pt"
    try:
        convert_and_upload(_pair)
    except KeyboardInterrupt:
        print("\nAborted by user.")
        sys.exit(130)
    except Exception as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        sys.exit(1)
