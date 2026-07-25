"""
Mirror sortformer and madlad models to tonythethompson/ on HuggingFace.

Downloads from source repos, uploads to new repos under tonythethompson/.
Run from repo root: python scripts/mirror_to_hf.py
"""

import os
import tempfile
from pathlib import Path
from huggingface_hub import HfApi, hf_hub_download, create_repo

api = HfApi()
ME = "tonythethompson"


def mirror(
    dest_repo: str,
    files: dict[str, tuple[str, str]],  # dest_path -> (src_repo, src_filename)
    repo_type: str = "model",
    commit_message: str = "Mirror from source repo",
) -> None:
    print(f"\n=== Mirroring to {dest_repo} ===")
    create_repo(dest_repo, repo_type=repo_type, exist_ok=True, private=False)

    for dest_path, (src_repo, src_filename) in files.items():
        print(f"  Downloading {src_repo}/{src_filename} ...")
        local = hf_hub_download(repo_id=src_repo, filename=src_filename, repo_type=repo_type)
        print(f"  Uploading -> {dest_path} ...")
        api.upload_file(
            path_or_fileobj=local,
            path_in_repo=dest_path,
            repo_id=dest_repo,
            repo_type=repo_type,
            commit_message=commit_message,
        )
        print(f"  Done: {dest_path}")

    print(f"=== {dest_repo} complete ===")


# --- Sortformer: cgus/diar_streaming_sortformer_4spk-v2.1-onnx ---
# One ONNX file, renamed to onnx/model.onnx for manifest compatibility.
SORTFORMER_SRC = "cgus/diar_streaming_sortformer_4spk-v2.1-onnx"
SORTFORMER_DEST = f"{ME}/diar-streaming-sortformer-4spk-v2.1-onnx"

mirror(
    dest_repo=SORTFORMER_DEST,
    files={
        "onnx/model.onnx": (SORTFORMER_SRC, "diar_streaming_sortformer_4spk-v2.1.onnx"),
    },
    commit_message="Mirror: diar_streaming_sortformer_4spk-v2.1 ONNX from cgus",
)

# --- Madlad: ISoloist1/madlad400-3b-mt-onnx ---
MADLAD_SRC = "ISoloist1/madlad400-3b-mt-onnx"
MADLAD_DEST = f"{ME}/madlad400-3b-mt-onnx"

mirror(
    dest_repo=MADLAD_DEST,
    files={
        "config.json":                    (MADLAD_SRC, "config.json"),
        "spiece.model":                   (MADLAD_SRC, "spiece.model"),
        "tokenizer_config.json":          (MADLAD_SRC, "tokenizer_config.json"),
        "encoder_model_quantized.onnx":   (MADLAD_SRC, "encoder_model_quantized.onnx"),
        "decoder_model_quantized.onnx":   (MADLAD_SRC, "decoder_model_quantized.onnx"),
    },
    commit_message="Mirror: madlad400-3b-mt ONNX quantized from ISoloist1",
)

print("\nAll done. Update manifest source_url / download_file_sources to new repos.")
