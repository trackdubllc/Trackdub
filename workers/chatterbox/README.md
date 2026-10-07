# Chatterbox worker

Real `.pt` TTS worker behind the sidecar protocol (`workers/PROTOCOL.md`).
Upstream weights: `ResembleAI/chatterbox` (MIT — license evidence in ADR-0006
for the Chatterbox family; the `.pt` artifact gets manifest-grade treatment
at `src/` promotion, not here).

## Layout

- `worker.py` — the worker. Protocol loop + envelopes always; torch and
  `chatterbox-tts` import lazily inside the load path only.
- `tests/test_protocol.py` — conformance suite. Stdlib only, no weights, no
  GPU. Drives the real `serve()` loop over piped stdio.
- `fixtures/` — shared request/response vectors (grows with parity work).

## Environment (pinned, per the supervision design)

```powershell
cd workers/chatterbox
uv python install 3.12
uv venv --python 3.12
uv sync --extra model   # installs torch (CUDA) + chatterbox-tts, writes uv.lock — commit it
```

Rules: never the system Python (3.14 on this host cannot run torch), no
`pip install` outside the lockfile, version stamp checked at startup by the
supervisor. The worker refuses to serve on mismatch rather than degrading.

## Running

```powershell
# Protocol conformance (no model needed):
python -m pytest tests/ -q

# Manual smoke (needs the model extra):
echo '{"id":"h","op":"health"}' | uv run worker.py
```

## Status

- [x] Protocol loop: health / load / infer / errors, typed envelopes
- [x] Lazy model stack (dependency-missing, never crash on import)
- [x] 8 conformance tests green without torch
- [x] Model env: `uv.lock` committed (torch 2.11+cu128, chatterbox-tts 0.1.7,
  Python 3.12); `uv sync --locked --extra model` reproduces it
- [x] First synthesis through the protocol (CUDA, 3.36 s, peak 29377, RMS 4275)
- [x] Voice-clone plumbing (`voicePromptPath` plan → `audio_prompt_path`;
  unreadable prompt fails load loudly, never silent default; same text
  default vs cloned differs in pacing and samples)
