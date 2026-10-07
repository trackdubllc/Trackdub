"""Protocol conformance tests for the Chatterbox worker.

These run with stdlib only — no torch, no weights, no GPU. Every test drives
the real `serve()` loop over piped stdio, so what passes here is the exact
byte behavior the Rust supervisor will speak to.
"""

import base64
import io
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from worker import PROTOCOL_VERSION, serve


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


def test_load_without_model_stack_is_dependency_missing_not_crash():
    (resp,) = run_worker([json.dumps({"id": "l1", "op": "load",
                                      "plan": {"model": "somewhere", "providers": ["CPU"]}})])
    assert resp["status"] == "error"
    assert resp["reason"] == "dependency-missing"
    assert "uv sync --extra model" in resp["detail"]


def test_load_without_plan_is_bad_plan():
    (resp,) = run_worker([json.dumps({"id": "l2", "op": "load"})])
    assert resp["status"] == "error"
    assert resp["reason"] == "bad-plan"


def test_every_response_carries_protocol_version():
    resps = run_worker([
        "{bad",
        json.dumps({"id": "x", "op": "nope"}),
        json.dumps({"id": "y", "op": "health"}),
    ])
    assert resps and all(r["protocolVersion"] == PROTOCOL_VERSION for r in resps)


def test_unreadable_voice_prompt_fails_load_loudly():
    (resp,) = run_worker([json.dumps({"id": "v1", "op": "load",
                                      "plan": {"model": "somewhere",
                                               "voicePromptPath": "/no/such/voice.wav"}})])
    assert resp["status"] == "error"
    assert resp["reason"] == "bad-plan"
    assert "voicePromptPath" in resp["detail"]


def test_readable_voice_prompt_passes_plan_validation():
    import tempfile, os
    fd, prompt = tempfile.mkstemp(suffix=".wav")
    os.close(fd)
    try:
        (resp,) = run_worker([json.dumps({"id": "v2", "op": "load",
                                          "plan": {"model": "somewhere",
                                                   "voicePromptPath": prompt}})])
    finally:
        os.unlink(prompt)
    # Whatever the model stack does next (loaded / load-failed /
    # dependency-missing), the prompt itself must not be the complaint.
    assert resp["status"] in ("loaded", "error")
    if resp["status"] == "error":
        assert resp["reason"] != "bad-plan" or "voicePromptPath" not in resp.get("detail", "")
    else:
        assert resp["voicePrompt"] == prompt
