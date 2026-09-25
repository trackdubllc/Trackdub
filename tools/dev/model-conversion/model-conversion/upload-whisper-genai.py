# /// script
# requires-python = ">=3.10"
# dependencies = ["huggingface_hub"]
# ///
from pathlib import Path
from runpy import run_path
run_path(Path(__file__).resolve().parent.parent / "upload-whisper-genai.py", run_name="__main__")
