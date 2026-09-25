# /// script
# requires-python = ">=3.10"
# dependencies = ["optimum[onnxruntime]", "transformers", "torch", "huggingface_hub", "sentencepiece"]
# ///
from pathlib import Path
from runpy import run_path
run_path(Path(__file__).resolve().parent.parent / "convert-tc-big-en-pt.py", run_name="__main__")
