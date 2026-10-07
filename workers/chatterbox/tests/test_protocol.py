"""Protocol conformance tests for the Chatterbox worker.

These run with stdlib only — no torch, no weights, no GPU. Every test drives
the real `serve()` loop over piped stdio, so what passes here is the exact
byte behavior the Rust supervisor will speak to.
"""

import io
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from worker import CHATTERBOX_REPO, PROTOCOL_VERSION, serve


def run_worker(lines):
    stdin = io.StringIO("".join(line + "\n" for line in lines))
    stdout = io.StringIO()
    old = sys.stdout
    sys.stdout = stdout
    try:
        serve(stdin=stdin)
    finally:
        sys.stdout = old
    return [json.loads(line) for line in stdout.getvalue().splitlines() if line.strip()]


def test_health_before_load_reports_unloaded():
    (resp,) = run_worker([json.dumps({"id": "h1", "op": "health"})])
    assert resp["status"] == "alive"
    assert resp["modelLoaded"] is False
    assert resp["protocolVersion"] == PROTOCOL_VERSION


def test_malformed_line_yields_invalid_json_and_loop_survives():
    resps = run_worker([
        "{not json",
        json.dumps({"id": "h2", "op": "health"}),
    ])
    assert resps[0]["status"] == "error"
    assert resps[0]["reason"] == "invalid-json"
    assert "id" not in resps[0]
    assert resps[1]["status"] == "alive"


def test_non_object_line_yields_invalid_json():
    (resp,) = run_worker(['[1,2,3]'])
    assert resp["reason"] == "invalid-json"


def test_unknown_op_names_itself():
    (resp,) = run_worker([json.dumps({"id": "u1", "op": "dance"})])
    assert resp["status"] == "error"
    assert resp["reason"] == "unknown-op"


def test_infer_before_load_is_no_model_loaded():
    (resp,) = run_worker([json.dumps({"id": "i1", "op": "infer", "inputs": {"text": "hi"}})])
    assert resp["status"] == "error"
    assert resp["reason"] == "no-model-loaded"


def test_load_without_model_stack_is_dependency_missing_not_crash(monkeypatch):
    import worker

    def _missing():
        raise ImportError("model stack missing: install with `uv sync --extra model` inside workers/chatterbox (stubbed)")

    monkeypatch.setattr(worker, "_import_model_stack", _missing)
    (resp,) = run_worker([json.dumps({"id": "l1", "op": "load",
                                      "plan": {"model": CHATTERBOX_REPO, "providers": ["CPU"]}})])
    assert resp["status"] == "error"
    assert resp["reason"] == "dependency-missing"
    assert "uv sync --extra model" in resp["detail"]


def test_load_without_plan_is_bad_plan():
    (resp,) = run_worker([json.dumps({"id": "l2", "op": "load"})])
    assert resp["status"] == "error"
    assert resp["reason"] == "bad-plan"


def test_load_of_non_string_model_is_bad_plan():
    (resp,) = run_worker([json.dumps({"id": "l3", "op": "load",
                                      "plan": {"model": 5, "providers": ["CPU"]}})])
    assert resp["status"] == "error"
    assert resp["reason"] == "bad-plan"


def test_load_of_unknown_repo_is_model_not_found(monkeypatch):
    import worker

    def _missing():
        raise AssertionError("model stack must not be touched for an unsupported model id")

    monkeypatch.setattr(worker, "_import_model_stack", _missing)
    (resp,) = run_worker([json.dumps({"id": "l4", "op": "load",
                                      "plan": {"model": "somewhere", "providers": ["CPU"]}})])
    assert resp["status"] == "error"
    assert resp["reason"] == "model-not-found"


def test_every_response_carries_protocol_version():
    resps = run_worker([
        "{bad",
        json.dumps({"id": "x", "op": "nope"}),
        json.dumps({"id": "y", "op": "health"}),
    ])
    assert resps and all(r["protocolVersion"] == PROTOCOL_VERSION for r in resps)


def test_unreadable_voice_prompt_fails_load_loudly():
    (resp,) = run_worker([json.dumps({"id": "v1", "op": "load",
                                      "plan": {"model": CHATTERBOX_REPO,
                                               "voicePromptPath": "/no/such/voice.wav"}})])
    assert resp["status"] == "error"
    assert resp["reason"] == "bad-plan"
    assert "voicePromptPath" in resp["detail"]


def test_readable_voice_prompt_passes_plan_validation(monkeypatch):
    import tempfile, os
    import worker

    def _missing():
        raise ImportError("model stack missing (stubbed)")

    monkeypatch.setattr(worker, "_import_model_stack", _missing)
    fd, prompt = tempfile.mkstemp(suffix=".wav")
    os.close(fd)
    try:
        (resp,) = run_worker([json.dumps({"id": "v2", "op": "load",
                                          "plan": {"model": CHATTERBOX_REPO,
                                                   "voicePromptPath": prompt}})])
    finally:
        os.unlink(prompt)
    # The readable prompt must pass plan validation; the only remaining
    # failure is the stubbed model stack, never bad-plan over voicePromptPath.
    assert resp["status"] == "error"
    assert resp["reason"] == "dependency-missing"
    assert "voicePromptPath" not in resp.get("detail", "")


def test_blank_line_yields_invalid_json_and_loop_survives():
    resps = run_worker([
        "",
        json.dumps({"id": "h3", "op": "health"}),
    ])
    assert resps[0]["status"] == "error"
    assert resps[0]["reason"] == "invalid-json"
    assert resps[1]["status"] == "alive"


def test_load_plan_honors_require_preferred(monkeypatch):
    import types
    import worker

    class _FakeCuda:
        @staticmethod
        def is_available():
            return False

    fake_torch = types.SimpleNamespace(cuda=_FakeCuda())
    fake_tts = types.SimpleNamespace()
    monkeypatch.setattr(worker, "_import_model_stack", lambda: (fake_torch, fake_tts))
    # CUDA is unavailable in this fake stack, so a hard-pinned CUDA plan must
    # fail the load instead of silently settling on CPU.
    (resp,) = run_worker([json.dumps({"id": "l5", "op": "load",
                                      "plan": {"model": CHATTERBOX_REPO,
                                               "providers": ["CUDA"],
                                               "requirePreferred": True}})])
    assert resp["status"] == "error"
    assert resp["reason"] == "load-failed"
    assert "CUDA" in resp["detail"]


def test_load_falls_back_within_approved_providers(monkeypatch):
    import types
    import worker

    class _FakeCuda:
        @staticmethod
        def is_available():
            return False

    fake_torch = types.SimpleNamespace(cuda=_FakeCuda())
    fake_tts = types.SimpleNamespace(from_pretrained=lambda device: object())
    monkeypatch.setattr(worker, "_import_model_stack", lambda: (fake_torch, fake_tts))
    (resp,) = run_worker([json.dumps({"id": "l6", "op": "load",
                                      "plan": {"model": CHATTERBOX_REPO,
                                               "providers": ["CUDA", "CPU"]}})])
    assert resp["status"] == "loaded"
    assert resp["activeProvider"] == "cpu"
    assert resp["model"] == CHATTERBOX_REPO
    worker._model = None
    worker._model_device = "none"
