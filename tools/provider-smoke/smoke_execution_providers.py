#!/usr/bin/env python3
"""Cross-EP smoke harness: session creation, EP discovery, and a model smoke run.

Complements the .NET `Trackdub.Benchmarks` provider-matrix scenario (which measures
the shipped pipeline stages end-to-end through the runtime planner) with a minimal,
dependency-light EP capability matrix: which execution providers load, which devices
they discover, and whether a reference ONNX model can be created/run through each.

The harness never fabricates success: each EP records one of
  ok            - session created and inference ran on that EP
  fallback      - session created but nodes landed on CPUExecutionProvider
  fail          - session creation or run raised, with the error message
  absent        - EP library/platform prerequisite missing (expected on wrong hosts)

Usage:
  python smoke_execution_providers.py --model <path.onnx> --results <out.json>
                                      [--providers openvino,qnn,migraphx,cpu]
                                      [--feed <npz-or-json>] [--repeat-runs 5]

Feed defaults to a 1x80x3000 float32 tensor named `input_features` (Whisper encoder
shape); pass `--feed` with a .npz file to smoke other models. Results are written
as JSON and echo to stdout. Requires the same ORT wheel variant as the EP under
test (e.g. `onnxruntime-openvino`, `onnxruntime-qnn`, `onnxruntime-migraphx`);
run each variant in its own virtual environment, or use `--providers` to scope.
"""
import argparse
import ctypes
import glob
import json
import os
import platform
import sys
import time

try:
    import numpy as np
except ImportError:
    print("numpy is required", file=sys.stderr)
    sys.exit(1)

try:
    import onnxruntime as ort
except ImportError:
    print("onnxruntime is required", file=sys.stderr)
    sys.exit(1)

# QNN provider libraries are shipped by the onnxruntime-qnn wheel in a sibling
# package (onnxruntime_qnn). The EP must be registered explicitly.
QNN_PROVIDER_LIB_CANDIDATES = (
    ("onnxruntime_qnn", "libonnxruntime_providers_qnn.so", "onnxruntime_providers_qnn.dll"),
)


def qnn_provider_library():
    for package, linux_name, windows_name in QNN_PROVIDER_LIB_CANDIDATES:
        try:
            module = __import__(package)
        except ImportError:
            continue
        lib_dir = os.path.dirname(os.path.abspath(module.__file__))
        for name in (linux_name, windows_name):
            path = os.path.join(lib_dir, name)
            if os.path.isfile(path):
                return path
    return None


def qnn_backend_path():
    module = __import__("onnxruntime_qnn")
    lib_dir = os.path.dirname(os.path.abspath(module.__file__))
    candidates = sorted(glob.glob(os.path.join(lib_dir, "libQnnHtp.so"))) or \
        sorted(glob.glob(os.path.join(lib_dir, "QnnHtp.dll")))
    return candidates[0] if candidates else None



def resolve_provider_name(name):
    """Map a short provider token to the exact ORT provider name, case-insensitively."""
    available = ort.get_available_providers()
    for candidate in available:
        if candidate.lower() == name.lower():
            return candidate
    for candidate in available:
        if candidate.lower().startswith(name.lower()) and candidate.lower().endswith("executionprovider"):
            return candidate
    return name

def register_qnn():
    lib = qnn_provider_library()
    if not lib:
        return "absent: onnxruntime-qnn provider package not installed"
    try:
        ort.register_execution_provider_library("QNNExecutionProvider", lib)
    except Exception as ex:
        if "already registered" not in str(ex).lower():
            return f"absent: {ex}"
    return None


DEFAULT_DYNAMIC_DIM = 3000
MAX_FEED_ELEMENTS = 12_000_000


def build_feed(model_path, feed_path):
    if feed_path:
        data = dict(np.load(feed_path))
        return {k: np.asarray(v, dtype=np.float32) for k, v in data.items()}
    sess = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
    rng = np.random.default_rng(42)
    feed = {}
    for spec in sess.get_inputs():
        shape = []
        for i, dim in enumerate(spec.shape):
            if isinstance(dim, int):
                shape.append(dim)
            elif i == 0:
                shape.append(1)
            else:
                shape.append(None)
        if any(d is None for d in shape):
            raise ValueError(
                f"Input '{spec.name}' has non-batch dynamic dims {spec.shape}; the smoke "
                "feed cannot be inferred generically. Provide an explicit --feed npz file "
                f"with a tensor named '{spec.name}'.")
        elements = int(np.prod(shape)) if shape else 1
        if elements > MAX_FEED_ELEMENTS:
            raise ValueError(
                f"Input '{spec.name}' shape {spec.shape} exceeds the smoke feed budget; "
                "provide an explicit --feed npz file.")
        if not shape:
            feed[spec.name] = np.zeros((), dtype=np.float32)
        else:
            feed[spec.name] = rng.standard_normal(shape).astype(np.float32)
    return feed


def bench_run(sess, feed, warmup, repeats):
    for _ in range(warmup):
        sess.run(None, feed)
    times = []
    for _ in range(repeats):
        start = time.perf_counter()
        sess.run(None, feed)
        times.append(time.perf_counter() - start)
    return {
        "best_ms": round(min(times) * 1000, 1),
        "mean_ms": round(sum(times) / len(times) * 1000, 1),
    }


def providers_used_by_profile(profile_path):
    """Return providers that executed profiled graph nodes."""
    with open(profile_path, encoding="utf-8") as handle:
        events = json.load(handle)
    providers = set()
    for event in events:
        provider = event.get("args", {}).get("provider")
        if provider:
            providers.add(provider)
    return providers


def smoke(model_path, provider_spec, feed, warmup, repeats):
    entry = {"provider_spec": provider_spec, "status": None, "detail": None}
    name, _, options = provider_spec.partition(":")
    provider_options = {}
    for part in options.split(",") if options else []:
        if "=" in part:
            key, value = part.split("=", 1)
            provider_options[key] = value
    if name.lower() == "qnn":
        problem = register_qnn()
        if problem:
            entry["status"] = "absent"
            entry["detail"] = problem
            return entry
        backend = qnn_backend_path()
        if backend and "backend_path" not in provider_options:
            provider_options["backend_path"] = backend
    try:
        requested = resolve_provider_name(name)
        session_options = ort.SessionOptions()
        session_options.enable_profiling = True
        start = time.perf_counter()
        if provider_options:
            sess = ort.InferenceSession(
                model_path, sess_options=session_options, providers=[(requested, provider_options)])
        else:
            sess = ort.InferenceSession(
                model_path, sess_options=session_options, providers=[requested])
        entry["session_create_s"] = round(time.perf_counter() - start, 2)
        providers_in_use = sess.get_providers()
        sess.run(None, feed)
        timing = bench_run(sess, feed, warmup, repeats)
        profile_path = sess.end_profiling()
        providers_used = providers_used_by_profile(profile_path)
        try:
            os.remove(profile_path)
        except OSError:
            pass
        entry.update(timing)
        if requested in providers_used:
            entry["status"] = "ok"
        else:
            entry["status"] = "fallback"
        entry["providers_in_use"] = providers_in_use
        entry["providers_used"] = sorted(providers_used)
    except Exception as ex:
        entry["status"] = "fail"
        entry["detail"] = str(ex)[:500]
    return entry


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True)
    parser.add_argument("--results", required=True)
    parser.add_argument("--providers", default="cpu,openvino")
    parser.add_argument("--feed")
    parser.add_argument("--warmup-runs", type=int, default=2)
    parser.add_argument("--repeat-runs", type=int, default=5)
    args = parser.parse_args()

    report = {
        "onnxruntime_version": ort.__version__,
        "python": sys.version.split()[0],
        "platform": platform.platform(),
        "cpu": platform.processor() or "unknown",
        "available_providers": list(ort.get_available_providers()),
        "model": os.path.abspath(args.model),
        "smokes": [],
    }

    feed = build_feed(args.model, args.feed)

    for spec in [s.strip() for s in args.providers.split(",") if s.strip()]:
        if spec.lower().startswith("coreml") and not sys.platform.startswith("darwin"):
            report["smokes"].append(
                {"provider_spec": spec, "status": "absent",
                 "detail": "CoreML EP requires macOS; not runnable on this host"})
            continue
        report["smokes"].append(smoke(args.model, spec, feed, args.warmup_runs, args.repeat_runs))

    os.makedirs(os.path.dirname(os.path.abspath(args.results)) or ".", exist_ok=True)
    with open(args.results, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
