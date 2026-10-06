#!/usr/bin/env python3
"""Cross-EP smoke harness: session creation, EP discovery, and a model smoke run.

Complements the .NET `Trackdub.Benchmarks` provider-matrix scenario (which measures
the shipped pipeline stages end-to-end through the runtime planner) with a minimal,
dependency-light EP capability matrix: which execution providers load, which devices
they discover, and whether a reference ONNX model can be created/run through each.

The harness never fabricates success: each EP records one of
  ok            - session created, inference ran, and >= 1 node executed on that EP
  fallback      - session created and inference ran, but 0 nodes executed on the
                  requested EP (every node landed elsewhere, typically CPU)
  fail          - session creation or run raised, with the error message
  absent        - EP library/platform prerequisite missing (expected on wrong hosts)

Node placement is derived from the ONNX Runtime profiler: each provider smoke runs
once with profiling enabled and counts kernel events by their `provider` field, so
`ok`/`fallback` reflect where nodes actually executed rather than which providers
the session merely registered. Per-EP node counts are recorded under `node_counts`.

Usage:
  python smoke_execution_providers.py --model <path.onnx> --results <out.json>
                                      [--providers openvino,qnn,migraphx,cpu]
                                      [--feed <npz>] [--repeat-runs 5]

Feed defaults to a 1x80x3000 float32 tensor named `input_features` (Whisper encoder
shape); pass `--feed` with a .npz file to smoke other models. Results are written
as JSON and echo to stdout. Requires the same ORT wheel variant as the EP under
test (e.g. `onnxruntime-openvino`, `onnxruntime-qnn`, `onnxruntime-migraphx`);
run each variant in its own virtual environment, or use `--providers` to scope.
Provider options use `;` as the separator after a colon, e.g.
`openvino:device_type=CPU;performance_hint=THROUGHPUT`.
"""
import argparse
import glob
import json
import os
import platform
import shutil
import sys
import tempfile
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


def _qnn_package_dir():
    """Directory of the onnxruntime-qnn sibling package, or None if not installed."""
    for package, _, _ in QNN_PROVIDER_LIB_CANDIDATES:
        try:
            module = __import__(package)
        except ImportError:
            continue
        return os.path.dirname(os.path.abspath(module.__file__))
    return None


def qnn_provider_library():
    lib_dir = _qnn_package_dir()
    if not lib_dir:
        return None
    for _, linux_name, windows_name in QNN_PROVIDER_LIB_CANDIDATES:
        for name in (linux_name, windows_name):
            path = os.path.join(lib_dir, name)
            if os.path.isfile(path):
                return path
    return None


def qnn_backend_path():
    lib_dir = _qnn_package_dir()
    if not lib_dir:
        return None
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


MAX_FEED_ELEMENTS = 12_000_000

# Generated feeds must match each input's declared ONNX type; unmapped types
# require an explicit --feed npz rather than a guessed tensor.
TYPE_TO_NUMPY = {
    "tensor(float)": np.float32,
    "tensor(float16)": np.float16,
    "tensor(double)": np.float64,
    "tensor(int64)": np.int64,
    "tensor(int32)": np.int32,
    "tensor(int16)": np.int16,
    "tensor(int8)": np.int8,
    "tensor(uint8)": np.uint8,
    "tensor(uint16)": np.uint16,
    "tensor(uint32)": np.uint32,
    "tensor(uint64)": np.uint64,
    "tensor(bool)": np.bool_,
}


def build_feed(model_path, feed_path):
    if feed_path:
        with np.load(feed_path) as data:
            return {k: np.asarray(v) for k, v in data.items()}
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
        dtype = TYPE_TO_NUMPY.get(spec.type)
        if dtype is None:
            raise ValueError(
                f"Input '{spec.name}' has type '{spec.type}' with no safe generated feed; "
                "provide an explicit --feed npz file.")
        if np.issubdtype(dtype, np.floating):
            feed[spec.name] = rng.standard_normal(shape).astype(dtype)
        else:
            feed[spec.name] = np.zeros(shape, dtype=dtype)
    return feed


def bench_run(sess, feed, warmup, repeats):
    if repeats < 1:
        raise ValueError("repeat_runs must be >= 1")
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


def parse_provider_options(options):
    provider_options = {}
    for part in options.split(";") if options else []:
        if "=" in part:
            key, value = part.split("=", 1)
            provider_options[key] = value
    return provider_options


def create_session(model_path, requested, provider_options):
    opts = ort.SessionOptions()
    if provider_options:
        return ort.InferenceSession(
            model_path, sess_options=opts, providers=[(requested, provider_options)])
    return ort.InferenceSession(model_path, sess_options=opts, providers=[requested])


def count_provider_nodes(profile_path):
    """Count kernel-time events per EP from an ORT profiling file."""
    counts = {}
    try:
        with open(profile_path, "r", encoding="utf-8") as handle:
            profile = json.load(handle)
    except (OSError, ValueError):
        return counts
    events = profile.get("traceEvents") if isinstance(profile, dict) else profile
    for event in events or []:
        if event.get("cat") != "Node":
            continue
        provider = (event.get("args") or {}).get("provider")
        if provider:
            counts[provider] = counts.get(provider, 0) + 1
    return counts


def profile_node_counts(model_path, requested, provider_options, feed):
    """Run the model once with profiling enabled and count node events per EP."""
    profiling_dir = tempfile.mkdtemp(prefix="provider-smoke-profile-")
    try:
        opts = ort.SessionOptions()
        opts.enable_profiling = True
        opts.profile_file_prefix = os.path.join(profiling_dir, "profile")
        if provider_options:
            sess = ort.InferenceSession(
                model_path, sess_options=opts, providers=[(requested, provider_options)])
        else:
            sess = ort.InferenceSession(model_path, sess_options=opts, providers=[requested])
        sess.run(None, feed)
        profile_path = sess.end_profiling()
        return count_provider_nodes(profile_path)
    finally:
        shutil.rmtree(profiling_dir, ignore_errors=True)


PREREQUISITE_MISSING_HINTS = (
    "Failed to load library",
    "Cannot load library",
    "cannot open shared object file",
    "Failed to load '",
)


def smoke(model_path, provider_spec, feed, warmup, repeats):
    entry = {"provider_spec": provider_spec, "status": None, "detail": None}
    name, _, options = provider_spec.partition(":")
    provider_options = parse_provider_options(options)
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
        if requested not in ort.get_available_providers():
            entry["status"] = "absent"
            entry["detail"] = (
                f"provider '{name}' is not available in this onnxruntime wheel "
                f"({ort.__version__}); install the matching EP wheel "
                "(e.g. onnxruntime-openvino for 'openvino').")
            return entry
        start = time.perf_counter()
        sess = create_session(model_path, requested, provider_options)
        entry["session_create_s"] = round(time.perf_counter() - start, 2)
        entry["providers_in_use"] = sess.get_providers()
        node_counts = profile_node_counts(model_path, requested, provider_options, feed)
        entry["node_counts"] = node_counts
        timing = bench_run(sess, feed, warmup, repeats)
        entry.update(timing)
        if node_counts.get(requested, 0) > 0:
            entry["status"] = "ok"
        else:
            entry["status"] = "fallback"
            entry["detail"] = (
                "session ran but 0 nodes executed on the requested provider; "
                f"nodes were placed on: {', '.join(sorted(node_counts)) or 'none'}")
    except Exception as ex:
        message = str(ex)
        if any(hint in message for hint in PREREQUISITE_MISSING_HINTS):
            entry["status"] = "absent"
            entry["detail"] = f"provider prerequisite missing on this host: {message[:500]}"
        else:
            entry["status"] = "fail"
            entry["detail"] = message[:500]
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
    if args.warmup_runs < 0:
        parser.error("--warmup-runs must be >= 0")
    if args.repeat_runs < 1:
        parser.error("--repeat-runs must be >= 1")

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