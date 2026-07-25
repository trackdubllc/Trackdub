#!/usr/bin/env python3
"""Port ElBruno.QwenTTS core into Trackdub.Inference.Onnx/Qwen3Tts with correct relative paths."""

from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCE_ROOT = REPO_ROOT / ".tmp" / "ElBruno.QwenTTS" / "src"
DEST_ROOT = REPO_ROOT / "src" / "Trackdub.Inference.Onnx" / "Qwen3Tts"

SKIP_FILES = {
    "ModelDownloader.cs",
    "VoiceCloningDownloader.cs",
    "QwenTtsServiceExtensions.cs",
    "OrtSessionHelper.cs",
    "ExecutionProvider.cs",
}

SOURCE_DIRS = [
    SOURCE_ROOT / "ElBruno.QwenTTS.Core",
    SOURCE_ROOT / "ElBruno.QwenTTS.VoiceCloning",
]

REPLACEMENTS = [
    (r"namespace ElBruno\.QwenTTS\.VoiceCloning", "namespace Trackdub.Inference.Onnx.Qwen3Tts"),
    (r"namespace ElBruno\.QwenTTS", "namespace Trackdub.Inference.Onnx.Qwen3Tts"),
    (r"using ElBruno\.QwenTTS\.VoiceCloning", "using Trackdub.Inference.Onnx.Qwen3Tts"),
    (r"using ElBruno\.QwenTTS", "using Trackdub.Inference.Onnx.Qwen3Tts"),
]

KEEP_FILES = {
    "Qwen3TtsEngine.cs",
    "Qwen3TtsModelFiles.cs",
    "Qwen3TtsVoiceCatalog.cs",
}


def transform(text: str) -> str:
    for pattern, replacement in REPLACEMENTS:
        text = re.sub(pattern, replacement, text)
    return text


def main() -> None:
    if not SOURCE_ROOT.exists():
        raise SystemExit(f"Missing source root: {SOURCE_ROOT}")

    for path in DEST_ROOT.rglob("*.cs"):
        if path.name in KEEP_FILES:
            continue
        path.unlink()

    for empty in sorted(DEST_ROOT.rglob("*"), reverse=True):
        if empty.is_dir() and not any(empty.iterdir()):
            empty.rmdir()

    count = 0
    for source_dir in SOURCE_DIRS:
        for source in source_dir.rglob("*.cs"):
            if source.name in SKIP_FILES:
                continue
            relative = source.relative_to(source_dir)
            target = DEST_ROOT / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(transform(source.read_text(encoding="utf-8")), encoding="utf-8")
            count += 1
            print(relative.as_posix())

    print(f"Ported {count} files to {DEST_ROOT}")


if __name__ == "__main__":
    main()
