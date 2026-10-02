---
name: xerj
description: Definition-first code search over locally indexed repos (xerj search / xerj def)
---

Use XERJ instead of grep when you need code you cannot see in the open files:

- `.\tools\ref-search.ps1 "<plain words>"` or `xerj search --prefix ref "<plain words>"` — search peer OSS reference repos (`sherpa-onnx`, `whisperX`, `kokoro-onnx`, `silero-vad`, `whisper.net`, `piper`).
- `.\tools\ref-search.ps1 "<plain words>" -Target trackdub` or `xerj search --prefix trackdub "<plain words>"` — search Trackdub core.
- `.\tools\ref-search.ps1 -Def "<symbol>"` or `xerj def --prefix <ref|trackdub> "<symbol>"` — go-to-definition.
- `xerj autoindex <folder>` — index a repo (once; re-index is incremental).
- MCP: the `xerj_search` tool accepts a plain string query and returns the same passages.

Trust the passage over memory: it is the actual code at that path. Cite file:line for anything you rely on.

