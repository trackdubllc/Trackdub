# /// script
# requires-python = ">=3.12"
# dependencies = ["onnxruntime-genai>=0.13.2", "onnx-ir>=0.2.1", "onnx>=1.21.0", "huggingface_hub", "transformers", "torch"]
# ///
from pathlib import Path
from runpy import run_path
run_path(Path(__file__).resolve().parent.parent / "build-upload-whisper-genai.py", run_name="__main__")
