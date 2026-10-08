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
import contextlib
import json
import sys

PROTOCOL_VERSION = 1
WORKER_STAMP = "trackdub-chatterbox-worker/0.1.0"

# Upstream model id. NOTE: ADR-0006's MIT evidence covered the Chatterbox ONNX
# entries and is superseded by the manifest hash-integrity policy; it does not
# cover this .pt repository. The .pt weights are fetched on first load into the
# Trackdub model cache, never vendored in the repo, and get manifest-grade
# provenance treatment at src/ promotion.
CHATTERBOX_REPO = "ResembleAI/chatterbox"

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


@contextlib.contextmanager
def _protocol_stdout():
    """Reserve stdout for the protocol while third-party model code runs.

    Model libraries print banners to stdout (e.g. perth's "loaded PerthNet
    (Implicit) at step 250,000"), which would interleave with the JSON-lines
    stream and corrupt the host's read. PROTOCOL.md gives stdout to the
    protocol; shunt everything else to stderr for the duration.
    """
    protocol_stdout = sys.stdout
    sys.stdout = sys.stderr
    try:
        yield
    finally:
        sys.stdout = protocol_stdout


def _import_model_stack():
    """Import torch + chatterbox lazily. Raises ImportError with a clear
    message when the model extra is not installed (`uv sync --extra model`)."""
    try:
        import torch
        from chatterbox.tts import ChatterboxTTS
    except ImportError as ex:
        raise ImportError(
            "model stack missing: install with `uv sync --extra model` "
            f"inside workers/chatterbox ({ex})"
        ) from ex
    return torch, ChatterboxTTS


def _resolve_device(providers, require_preferred, torch):
    """Pick the device from the planner's ordered providers (rule 3: the
    worker never selects providers; it only walks the plan's list). Returns
    None when no permitted provider is available — never a silent fallback.

    When the plan carries no provider list the planner left the choice open,
    so the worker settles on CUDA when present, else CPU, and reports it.
    """
    def _available(provider):
        name = str(provider).upper()
        if name == "CUDA":
            return "cuda" if torch.cuda.is_available() else None
        if name == "CPU":
            return "cpu"
        return None  # unknown provider is not available

    if not providers:
        return "cuda" if torch.cuda.is_available() else "cpu"
    if require_preferred:
        return _available(providers[0])
    for provider in providers:
        settled = _available(provider)
        if settled is not None:
            return settled
    return None


def handle_load(request_id, plan):
    global _model, _model_device, _voice_prompt
    if not isinstance(plan, dict) or not isinstance(plan.get("model"), str) or not plan["model"]:
        respond(request_id, "error", reason="bad-plan",
                detail="load requires plan.model (repo id or local path)")
        return
    providers = plan.get("providers")
    if providers is not None and not isinstance(providers, list):
        respond(request_id, "error", reason="bad-plan",
                detail="plan.providers must be a list of provider names")
        return
    # Validate cheap things before touching the model stack: a voice prompt
    # that cannot be read fails the load loudly. No silent fallback to the
    # default voice — a dubbing take with the wrong voice is a wrong take.
    voice_prompt = plan.get("voicePromptPath")
    if voice_prompt is not None:
        from pathlib import Path as _Path
        if not _Path(str(voice_prompt)).is_file():
            respond(request_id, "error", reason="bad-plan",
                    detail=f"voicePromptPath unreadable: {voice_prompt}")
            return
    from pathlib import Path

    ckpt = Path(str(plan["model"]))
    if not ckpt.is_dir():
        # from_pretrained(device) always resolves the library's fixed upstream
        # repo; it takes a device, not a model id. The worker therefore only
        # reaches it for that exact repo — anything else answers model-not-found
        # rather than silently loading a different model.
        if str(plan["model"]) != CHATTERBOX_REPO:
            respond(request_id, "error", reason="model-not-found",
                    detail=f"unsupported non-directory model id: {plan['model']!r} "
                          f"(expected {CHATTERBOX_REPO} or a local snapshot dir)")
            return
    try:
        # Import inside the protocol-stdout guard: torch/chatterbox/transformers
        # imports can print banners at import time (the perth class of bug), and
        # any import-time stdout write would land in the protocol stream ahead
        # of the "loaded" response. Only Python-level writes are redirected;
        # native-fd writes cannot be swapped this way and remain a known gap.
        with _protocol_stdout():
            torch, ChatterboxTTS = _import_model_stack()
    except ImportError as ex:
        respond(request_id, "error", reason="dependency-missing", detail=str(ex))
        return
    device = _resolve_device(providers or [], bool(plan.get("requirePreferred")), torch)
    if device is None:
        wanted = providers[0] if bool(plan.get("requirePreferred")) and providers \
            else ", ".join(str(p) for p in providers)
        respond(request_id, "error", reason="load-failed",
                detail=f"no available provider honoring plan "
                      f"({'required ' if plan.get('requirePreferred') else ''}{wanted})")
        return
    try:
        with _protocol_stdout():
            if ckpt.is_dir():
                # from_local(ckpt_dir, device) — verified against chatterbox-tts 0.1.7.
                _model = ChatterboxTTS.from_local(ckpt, device)
            else:
                _model = ChatterboxTTS.from_pretrained(device)
        _model_device = device
        _voice_prompt = str(voice_prompt) if voice_prompt is not None else None
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
                detail="infer requires inputs.text (plain string, or a {dtype: 'utf8', shape, data} envelope)")
        return
    text = inputs["text"]
    if isinstance(text, dict):
        if text.get("dtype") != "utf8":
            respond(request_id, "error", reason="bad-inputs",
                    detail=f"text envelope dtype must be 'utf8', got {text.get('dtype')!r}")
            return
        try:
            text = base64.b64decode(text["data"]).decode("utf-8")
        except Exception as ex:
            respond(request_id, "error", reason="bad-inputs", detail=f"text envelope undecodable: {ex}")
            return
    try:
        import torch

        with torch.inference_mode(), _protocol_stdout():
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


def serve(stdin=None):
    """Serve until EOF. The `stdin` param exists for tests; production reads
    real stdio. Every line — including blank ones — gets one response, so the
    one-line-in, one-line-out contract holds."""
    stream = stdin if stdin is not None else sys.stdin
    for line in stream:
        handle_line(line)


def main():
    serve()


if __name__ == "__main__":
    main()
