"""Chatterbox TTS inference worker behind the Trackdub sidecar protocol.

JSON-lines on stdin/stdout; typed `{dtype, shape, data}` tensor envelopes;
planner-approved load plans. Normative contract: `workers/PROTOCOL.md`.

torch and chatterbox-tts are LAZY imports inside the load path, so importing
this module — and the whole protocol test suite — needs nothing but the
stdlib. A worker that cannot import the model stack answers `dependency-missing`
instead of crashing, which is exactly the honest-readiness behavior the
supervisor gates on.
"""

from __future__ import annotations

import base64
import io
import json
import struct
import sys
import wave

PROTOCOL_VERSION = 1
WORKER_STAMP = "trackdub-chatterbox-worker/0.1.0"

# Upstream model id (MIT, per ADR-0006 license evidence for the Chatterbox family).
# The .pt weights are fetched on first load into the Trackdub model cache and
# never vendored in the repo.
CHATTERBOX_REPO = "ResembleAI/chatterbox"

_model = None
_model_device = "none"


def respond(request_id, status, reason=None, **extra):
    payload = {"status": status, "protocolVersion": PROTOCOL_VERSION}
    if request_id is not None:
        payload["id"] = request_id
    if reason is not None:
        payload["reason"] = reason
    payload.update(extra)
    sys.stdout.write(json.dumps(payload) + "\n")
    sys.stdout.flush()


def invalid_json(detail):
    respond(None, "error", reason="invalid-json", detail=str(detail))


def handle_health(request_id):
    respond(
        request_id,
        "alive",
        modelLoaded=_model is not None,
        activeProvider=_model_device,
        worker=WORKER_STAMP,
    )


def _import_model_stack():
    """Import torch + chatterbox lazily. Raises ImportError with a clear
    message when the model extra is not installed (`uv sync --extra model`)."""
    try:
        import torch  # noqa: F401
        from chatterbox.tts import ChatterboxTTS  # noqa: F401
    except ImportError as ex:
        raise ImportError(
            "model stack missing: install with `uv sync --extra model` "
            f"inside workers/chatterbox ({ex})"
        ) from ex
    import torch
    from chatterbox.tts import ChatterboxTTS

    return torch, ChatterboxTTS


def _resolve_device():
    try:
        import torch
    except ImportError:
        return "cpu"
    if torch.cuda.is_available():
        return "cuda"
    return "cpu"


def handle_load(request_id, plan):
    global _model, _model_device
    if not isinstance(plan, dict) or not plan.get("model"):
        respond(request_id, "error", reason="bad-plan",
                detail="load requires plan.model (repo id or local path)")
        return
    try:
        torch, ChatterboxTTS = _import_model_stack()
    except ImportError as ex:
        respond(request_id, "error", reason="dependency-missing", detail=str(ex))
        return
    device = _resolve_device()
    try:
        # from_pretrained resolves HF repo ids AND local snapshot dirs, so the
        # planner can hand either a cache path or ResembleAI/chatterbox.
        # NOTE (verify against installed chatterbox-tts at integration time):
        # current API is ChatterboxTTS.from_pretrained(device).
        _model = ChatterboxTTS.from_pretrained(plan["model"] if "/" in plan["model"] or "\\" in plan["model"] else CHATTERBOX_REPO)
        if device == "cuda" and hasattr(_model, "to"):
            _model = _model.to(device)
        _model_device = device
    except Exception as ex:  # model download / native load failure: report, don't crash
        _model, _model_device = None, "none"
        respond(request_id, "error", reason="load-failed", detail=f"{type(ex).__name__}: {ex}")
        return
    respond(request_id, "loaded", activeProvider=_model_device, model=str(plan["model"]))


def _tensor_envelope(dtype, shape, raw_bytes):
    return {
        "dtype": dtype,
        "shape": list(shape),
        "data": base64.b64encode(raw_bytes).decode("ascii"),
    }


def handle_infer(request_id, inputs):
    if _model is None:
        respond(request_id, "error", reason="no-model-loaded")
        return
    if not isinstance(inputs, dict) or "text" not in inputs:
        respond(request_id, "error", reason="bad-inputs",
                detail="infer requires inputs.text ({dtype, shape, data} envelope is reserved for tensor models)")
        return
    text = inputs["text"]
    if isinstance(text, dict):
        try:
            text = base64.b64decode(text["data"]).decode("utf-8")
        except Exception as ex:
            respond(request_id, "error", reason="bad-inputs", detail=f"text envelope undecodable: {ex}")
            return
    try:
        import torch

        with torch.inference_mode():
            # Voice to clone comes from the load plan in the full build
            # (audio_prompt_path); v0.1 synthesizes the default voice so the
            # protocol path is exercisable before voice plumbing lands.
            wav = _model.generate(str(text))
        pcm = (wav.cpu().numpy().clip(-1.0, 1.0) * 32767).astype("<i2").tobytes()
        sample_rate = int(getattr(_model, "sr", 24000))
    except Exception as ex:
        respond(request_id, "error", reason="infer-failed", detail=f"{type(ex).__name__}: {ex}")
        return
    respond(
        request_id,
        "ok",
        outputs={"audio": _tensor_envelope("int16", [len(pcm) // 2], pcm)},
        sampleRate=sample_rate,
    )


def handle_line(line):
    try:
        req = json.loads(line)
    except json.JSONDecodeError as ex:
        invalid_json(ex)
        return
    if not isinstance(req, dict):
        invalid_json("request must be a JSON object")
        return
    request_id = req.get("id")
    op = req.get("op")
    if op == "health":
        handle_health(request_id)
    elif op == "load":
        handle_load(request_id, req.get("plan"))
    elif op == "infer":
        handle_infer(request_id, req.get("inputs"))
    else:
        respond(request_id, "error", reason="unknown-op", detail=f"op={op!r}")


def serve(stdin=None, stdout=None):
    """Serve until EOF. stdio params exist for tests; production uses real stdio."""
    stream = stdin if stdin is not None else sys.stdin
    for line in stream:
        if line.strip():
            handle_line(line)


def main():
    serve()


if __name__ == "__main__":
    main()
