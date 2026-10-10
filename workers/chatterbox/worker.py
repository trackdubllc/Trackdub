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
from pathlib import Path

SUPPORTED_PROVIDERS = {"CPU": "cpu", "CUDA": "cuda"}
SUPPORTED_TEXT_DTYPE = "utf8"

_MODEL_NOT_FOUND = "model path must be an existing local directory"
_VOICE_CLONING_DISABLED = "voice cloning is not authorized through the sidecar"

# No remote/default resolution is permitted here: the host must provide a
# planner-approved, integrity-qualified local model directory.

# Consent is deliberately not represented by a client-controlled wire value.
# Until the trusted host can attach a session-scoped authorization, reference
# voices are refused rather than allowing direct sidecar requests to bypass it.
_VOICE_CLONING_REQUIRES_HOST_AUTHORIZATION = True


def _planned_device(plan, torch):
    providers = plan.get("providers")
    if not isinstance(providers, list) or not providers:
        raise ValueError("load requires a non-empty providers list")
    candidates = []
    for provider in providers:
        device = SUPPORTED_PROVIDERS.get(str(provider).upper())
        if device is not None and device not in candidates:
            candidates.append(device)
    if not candidates:
        raise ValueError("plan has no supported providers (expected CUDA or CPU)")
    if plan.get("requirePreferred"):
        candidates = candidates[:1]
    for device in candidates:
        if device == "cpu" or torch.cuda.is_available():
            return device
    raise RuntimeError("no planned provider is available")


def _decode_text_envelope(value):
    if not isinstance(value, dict):
        raise ValueError("inputs.text must be a utf8 tensor envelope")
    if value.get("dtype") != SUPPORTED_TEXT_DTYPE:
        raise ValueError("inputs.text dtype must be utf8")
    raw = base64.b64decode(value["data"], validate=True)
    shape = value.get("shape")
    if shape != [len(raw)]:
        raise ValueError("inputs.text shape must equal its UTF-8 byte count")
    return raw.decode("utf-8")

PROTOCOL_VERSION = 1
WORKER_STAMP = "trackdub-chatterbox-worker/0.1.0"

_model = None
_model_device = "none"
_voice_prompt = None


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


def handle_load(request_id, plan):
    global _model, _model_device, _voice_prompt
    if not isinstance(plan, dict) or not plan.get("model"):
        respond(request_id, "error", reason="bad-plan",
                detail="load requires plan.model (local model directory)")
        return
    # Validate cheap things before touching the model stack: a voice prompt
    # that cannot be read fails the load loudly. No silent fallback to the
    # default voice — a dubbing take with the wrong voice is a wrong take.
    voice_prompt = plan.get("voicePromptPath")
    if voice_prompt is not None:
        if not Path(str(voice_prompt)).is_file():
            respond(request_id, "error", reason="bad-plan",
                    detail=f"voicePromptPath unreadable: {voice_prompt}")
            return
        if _VOICE_CLONING_REQUIRES_HOST_AUTHORIZATION:
            respond(request_id, "error", reason="load-not-implemented",
                    detail=_VOICE_CLONING_DISABLED)
            return
    try:
        torch, ChatterboxTTS = _import_model_stack()
    except ImportError as ex:
        respond(request_id, "error", reason="dependency-missing", detail=str(ex))
        return
    try:
        device = _planned_device(plan, torch)
        ckpt = Path(str(plan["model"]))
        # from_pretrained(device) ignores the requested identity and can fetch
        # an unrelated default model. Only the planner-approved local directory
        # form is supported until repository resolution can preserve identity.
        if not ckpt.is_dir():
            respond(request_id, "error", reason="model-not-found", detail=_MODEL_NOT_FOUND)
            return
        _model = ChatterboxTTS.from_local(ckpt, device)
        _model_device = device
        _voice_prompt = None
    except Exception as ex:  # model download / native load failure: report, don't crash
        _model, _model_device, _voice_prompt = None, "none", None
        respond(request_id, "error", reason="load-failed", detail=f"{type(ex).__name__}: {ex}")
        return
    loaded = {"activeProvider": _model_device, "model": str(plan["model"])}
    if _voice_prompt is not None:
        loaded["voicePrompt"] = _voice_prompt
    respond(request_id, "loaded", **loaded)


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
                detail="infer requires inputs.text as a utf8 tensor envelope")
        return
    try:
        text = _decode_text_envelope(inputs["text"])
    except (KeyError, TypeError, ValueError) as ex:
        respond(request_id, "error", reason="bad-inputs", detail=f"text envelope undecodable: {ex}")
        return
    try:
        import torch

        with torch.inference_mode():
            generate_kwargs = {}
            if _voice_prompt is not None:
                generate_kwargs["audio_prompt_path"] = _voice_prompt
            wav = _model.generate(str(text), **generate_kwargs)
        pcm = (wav.cpu().numpy().clip(-1.0, 1.0) * 32767).astype("<i2").tobytes()
        sample_rate = int(getattr(_model, "sr", 24000))
    except Exception as ex:
        respond(request_id, "error", reason="infer-failed", detail=f"{type(ex).__name__}: {ex}")
        return
    ok = {
        "outputs": {"audio": _tensor_envelope("int16", [len(pcm) // 2], pcm)},
        "sampleRate": sample_rate,
    }
    if _voice_prompt is not None:
        ok["voicePrompt"] = _voice_prompt
    respond(request_id, "ok", **ok)


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
