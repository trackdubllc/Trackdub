#!/usr/bin/env python3
"""Out-of-process separation runner for the Bandit v2 multilingual cinematic checkpoint.

Speaks the same command line as `Trackdub.Benchmarks separation-eval`, so `run_eval.py` drives it with
`--runner "<python> <this file>"` and scores it with the unchanged scorer:

    separation-eval --jobs <jobs.jsonl> --results <results.jsonl> --model <candidate> [--provider cpu|cuda]
                    --model-directory <dir holding checkpoint-multi.ckpt and repo/>

`--model` selects how the bed is formed (see below); `--model-directory` must contain the Zenodo
checkpoint (`checkpoint-multi.ckpt`, record 12701995) and a checkout of kwatcharasupat/bandit-v2 (`repo/`).
Only the model class and the official chunked inference handler are imported from that checkout; the Hydra,
Ray and Lightning system is not used. The checkpoint is loaded with `weights_only=True`, so nothing in it is
unpickled as code, and with `strict=True`, so a mismatched architecture fails instead of running with
random weights.

Pipeline per job, all of it recorded in the result metadata:
  1. read the 44.1 kHz stereo PCM16 input the scorer prepared (mixture, float);
  2. resample each channel to the model's native 48 kHz (torchaudio sinc resample);
  3. run the official 8 s / 1 s-hop chunked inference, each channel independently (the model is mono);
  4. resample the `speech` estimate back to 44.1 kHz and fix the length to the input length;
  5. bed:
       `sum`      music + sfx estimates, each resampled back (the model's own bed);
       `residual` input mixture - dialogue, computed in the 44.1 kHz domain after step 4;
  6. write float32 stereo WAV stems, so no output quantisation is declared.
A job fails (and is recorded as failed, with no stems left behind) on any exception, on non-finite model
output, or on a length mismatch it cannot explain; failures are never recorded as silent output.
"""

from __future__ import annotations

import argparse
import contextlib
import ctypes
import hashlib
import io
import json
import os
import subprocess
import sys
import time
import types
from pathlib import Path

os.environ.setdefault("TQDM_DISABLE", "1")

NATIVE_SR = 48000
SEPARATOR_SR = 44100
CHECKPOINT_NAME = "checkpoint-multi.ckpt"
CHECKPOINT_MD5 = "fea2868787551b0cff36cfcf7c3622a3"  # Zenodo 12701995 file listing
STEMS = ["speech", "music", "sfx"]  # configs/data/dnr-v3-com-smad-multi-v2b.yaml
BED_MODES = {"bandit-v2-multi-sum": "sum", "bandit-v2-multi-residual": "residual"}
# configs/inference/chunked-tensor-a100.yaml uses a batch of 16 for an A100; 16 x 8 s needs 14.8 GB here and spills
# out of the 12 GB card (about 12 s per batch). Batch size does not change the maths, so 4 (3.8 GB) is used.
CHUNK_SECONDS, HOP_SECONDS, BATCH = 8.0, 1.0, 4
LENGTH_FIX_MAX = 4


class RunnerError(Exception):
    pass


def parse(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(add_help=True)
    p.add_argument("command", choices=["separation-eval"])
    p.add_argument("--jobs", required=True, type=Path)
    p.add_argument("--results", required=True, type=Path)
    p.add_argument("--model", required=True, choices=sorted(BED_MODES))
    p.add_argument("--provider", default=None)
    p.add_argument("--model-directory", required=True, type=Path)
    p.add_argument("--model-cache-directory", default=None)  # accepted for command-line compatibility
    return p.parse_args(argv)


def _memory_counters():
    """Working-set counters of this process (Windows). Returns (peak, current) bytes."""
    from ctypes import wintypes

    class Counters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                    ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]

    kernel32, psapi = ctypes.WinDLL("kernel32"), ctypes.WinDLL("psapi")
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    c = Counters()
    c.cb = ctypes.sizeof(c)
    if not psapi.GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(c), c.cb):
        raise RunnerError("GetProcessMemoryInfo failed")
    return int(c.PeakWorkingSetSize), int(c.WorkingSetSize)


def peak_working_set_bytes() -> int:
    return _memory_counters()[0]


def current_working_set_bytes() -> int:
    return _memory_counters()[1]


def file_hash(path: Path, algo: str) -> str:
    h = hashlib.new(algo)
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def git_head(repo: Path) -> str:
    out = subprocess.run(["git", "-C", str(repo), "rev-parse", "HEAD"], capture_output=True, text=True, check=False)
    if out.returncode != 0:
        raise RunnerError(f"cannot read the revision of {repo}: {out.stderr.strip()}")
    dirty = subprocess.run(["git", "-C", str(repo), "status", "--porcelain", "--untracked-files=no"],
                           capture_output=True, text=True, check=False).stdout.strip()
    return out.stdout.strip() + ("+dirty" if dirty else "")


class Separator:
    def __init__(self, model_directory: Path, provider: str | None):
        import torch
        import torchaudio

        self.torch, self.ta = torch, torchaudio
        repo = model_directory / "repo"
        checkpoint = model_directory / CHECKPOINT_NAME
        if not (repo / "src" / "models" / "bandit" / "bandit.py").is_file():
            raise RunnerError(f"no bandit-v2 checkout under {repo}")
        if not checkpoint.is_file():
            raise RunnerError(f"checkpoint not found: {checkpoint}")
        self.checkpoint_md5 = file_hash(checkpoint, "md5")
        if self.checkpoint_md5 != CHECKPOINT_MD5:
            raise RunnerError(f"checkpoint md5 {self.checkpoint_md5} does not match the Zenodo listing {CHECKPOINT_MD5}")
        self.checkpoint_sha256 = file_hash(checkpoint, "sha256")
        self.repo_revision = git_head(repo)

        # The official handler imports torchaudio.io.StreamReader for its streaming variants only; the
        # tensor handler used here never touches it, and current torchaudio no longer ships the module.
        sys.modules.setdefault("torchaudio.io", types.SimpleNamespace(StreamReader=object))
        sys.path.insert(0, str(repo))
        from src.models.bandit.bandit import Bandit
        from src.system.inference_handler import StandardTensorChunkedInferenceHandler

        wanted = (provider or "").lower()
        if wanted in ("", "cuda", "gpu"):
            if torch.cuda.is_available():
                self.device = torch.device("cuda")
            elif wanted:
                raise RunnerError("provider cuda requested but CUDA is not available")
            else:
                self.device = torch.device("cpu")
        elif wanted == "cpu":
            self.device = torch.device("cpu")
        else:
            raise RunnerError(f"unsupported provider {provider!r}; use cpu or cuda")

        model = Bandit(in_channels=1, stems=STEMS, band_type="musical", n_bands=64,
                       normalize_channel_independently=False, treat_channel_as_feature=True, n_sqm_modules=8,
                       emb_dim=128, rnn_dim=256, bidirectional=True, rnn_type="GRU", mlp_dim=512,
                       hidden_activation="Tanh", hidden_activation_kwargs=None, complex_mask=True,
                       use_freq_weights=True, n_fft=2048, win_length=2048, hop_length=512,
                       window_fn="hann_window", wkwargs=None, power=None, center=True, normalized=True,
                       pad_mode="reflect", onesided=True, fs=NATIVE_SR)
        state = torch.load(checkpoint, map_location="cpu", weights_only=True)
        self.checkpoint_epoch = state.get("epoch")
        self.checkpoint_step = state.get("global_step")
        prefix = "model."
        weights = {k[len(prefix):]: v for k, v in state["state_dict"].items() if k.startswith(prefix)}
        # Training-only loss weights (`loss_handler.*`) ride along in the Lightning state dict; anything else
        # outside the model is unexpected and fails the load.
        extra = [k for k in state["state_dict"] if not k.startswith(prefix) and not k.startswith("loss_handler.")]
        if extra:
            raise RunnerError(f"checkpoint has {len(extra)} state entries outside the model (first: {extra[0]})")
        model.load_state_dict(weights, strict=True)
        self.model = model.to(self.device).eval()
        self.handler = StandardTensorChunkedInferenceHandler(
            chunk_size_seconds=CHUNK_SECONDS, hop_size_seconds=HOP_SECONDS, inference_batch_size=BATCH,
            fs=NATIVE_SR, window_fn="hann_window", pad_mode="reflect").to(self.device).eval()
        self.versions = {"torch": torch.__version__, "torchaudio": torchaudio.__version__,
                         "cuda": str(torch.version.cuda), "python": sys.version.split()[0]}
        self.device_name = torch.cuda.get_device_name(0) if self.device.type == "cuda" else "cpu"

    def estimates(self, stereo44) -> dict:
        """stereo44: float32 tensor (2, n) at 44.1 kHz. Returns {stem: (2, n) float32 tensor at 44.1 kHz}."""
        torch, ta = self.torch, self.ta
        n = stereo44.shape[1]
        x = ta.functional.resample(stereo44.to(self.device), SEPARATOR_SR, NATIVE_SR)
        with torch.inference_mode(), contextlib.redirect_stdout(io.StringIO()):
            out = self.handler(x[None, :, :], self.model)["estimates"]
        result = {}
        for stem, v in out.items():
            y = v["audio"][0]
            if y.shape[0] != 2 or not bool(torch.isfinite(y).all()):
                raise RunnerError(f"stem {stem} is not finite stereo output (shape {tuple(y.shape)})")
            back = ta.functional.resample(y.float(), NATIVE_SR, SEPARATOR_SR)
            result[stem] = fix_length(torch, back, n).cpu()
        return result


def fix_length(torch, x, n: int):
    delta = x.shape[1] - n
    if abs(delta) > LENGTH_FIX_MAX:
        raise RunnerError(f"resampled length differs from the input by {delta} samples")
    if delta > 0:
        return x[:, :n]
    if delta < 0:
        return torch.nn.functional.pad(x, (0, -delta))
    return x


def read_wav_float(path: str):
    import soundfile as sf
    import torch
    data, rate = sf.read(path, dtype="float32", always_2d=True)
    if rate != SEPARATOR_SR or data.shape[1] != 2:
        raise RunnerError(f"expected 44.1 kHz stereo input, got {rate} Hz x {data.shape[1]} channels")
    return torch.from_numpy(data.T.copy())


def write_wav_float(path: str, x) -> None:
    import soundfile as sf
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    sf.write(path, x.numpy().T, SEPARATOR_SR, subtype="FLOAT")


def run_job(sep: Separator, job: dict, index: int, bed_mode: str, provider: str, base_meta: dict) -> dict:
    torch = sep.torch
    before = current_working_set_bytes()
    started = time.perf_counter()
    record = {"id": job["id"], "job_index": index, "ok": False, "error": None, "wall_ms": 0.0,
              "audio_seconds": 0.0, "rtf": None, "working_set_before_bytes": before,
              "peak_working_set_bytes": None, "requested_provider": provider,
              "selected_provider": sep.device.type, "bootstrap_detail": None, "metadata": dict(base_meta)}
    written = []
    try:
        mix = read_wav_float(job["input"])
        n = mix.shape[1]
        record["audio_seconds"] = n / SEPARATOR_SR
        est = sep.estimates(mix)
        dialogue = est["speech"]
        bed = est["music"] + est["sfx"] if bed_mode == "sum" else mix - dialogue
        for name, stem in (("vocals_output", dialogue), ("bed_output", bed)):
            if stem.shape != mix.shape or not bool(torch.isfinite(stem).all()):
                raise RunnerError(f"{name} is not finite or not the input's shape")
            write_wav_float(job[name], stem)
            written.append(job[name])
        record["ok"] = True
    except Exception as exc:  # noqa: BLE001 - every failure is recorded per job, never as silent output
        record["error"] = f"{type(exc).__name__}: {exc}"
        for path in written:
            Path(path).unlink(missing_ok=True)
    elapsed = time.perf_counter() - started
    record["wall_ms"] = elapsed * 1000.0
    if record["ok"] and record["audio_seconds"] > 0:
        record["rtf"] = elapsed / record["audio_seconds"]
    record["peak_working_set_bytes"] = peak_working_set_bytes()
    if sep.device.type == "cuda":
        record["metadata"]["peak_cuda_allocated_bytes"] = str(sep.torch.cuda.max_memory_allocated())
        sep.torch.cuda.reset_peak_memory_stats()
    return record


def main(argv: list[str]) -> int:
    args = parse(argv)
    bed_mode = BED_MODES[args.model]
    try:
        sep = Separator(args.model_directory, args.provider)
    except RunnerError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    jobs = [json.loads(line) for line in args.jobs.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.lstrip().startswith("#")]
    runner_sha = file_hash(Path(__file__), "sha256")
    meta = {"runner": "bandit-v2-python", "engine_family": "bandit-v2", "model": args.model,
            "checkpoint": CHECKPOINT_NAME, "checkpoint_md5": sep.checkpoint_md5,
            "checkpoint_sha256": sep.checkpoint_sha256, "checkpoint_epoch": str(sep.checkpoint_epoch),
            "checkpoint_global_step": str(sep.checkpoint_step), "weights_source": "zenodo.org/records/12701995",
            "code_repo": "github.com/kwatcharasupat/bandit-v2", "code_revision": sep.repo_revision,
            "runner_sha256": runner_sha, "bed_mode": bed_mode, "stems": ",".join(STEMS),
            "native_sample_rate": str(NATIVE_SR), "separator_sample_rate": str(SEPARATOR_SR),
            "resample": "torchaudio.functional.resample sinc, 44100->48000 on input and 48000->44100 on every stem",
            "chunk_seconds": str(CHUNK_SECONDS), "hop_seconds": str(HOP_SECONDS), "inference_batch": str(BATCH),
            "channels": "each input channel separated independently as mono",
            "output": "float32 stereo WAV", "device": sep.device_name,
            "selected_provider": sep.device.type,
            "versions": json.dumps(sep.versions, sort_keys=True)}
    if bed_mode == "residual":
        meta["bed_formula"] = "input mixture - dialogue estimate, both 44.1 kHz stereo, after length restoration"
    else:
        meta["bed_formula"] = "music estimate + sfx estimate, each resampled to 44.1 kHz"
    provider = args.provider or sep.device.type

    # Warm-up: one untimed pass so CUDA context, cuDNN and allocator start-up are not billed to the first clip.
    warm_started = time.perf_counter()
    try:
        sep.estimates(sep.torch.zeros(2, SEPARATOR_SR * 2) + 1e-3 * sep.torch.randn(2, SEPARATOR_SR * 2))
    except Exception as exc:  # noqa: BLE001
        print(f"error: warm-up failed: {exc}", file=sys.stderr)
        return 1
    meta["warmup_ms"] = f"{(time.perf_counter() - warm_started) * 1000.0:.0f}"
    if sep.device.type == "cuda":
        sep.torch.cuda.reset_peak_memory_stats()

    failed = 0
    args.results.parent.mkdir(parents=True, exist_ok=True)
    with args.results.open("w", encoding="utf-8") as out:
        for index, job in enumerate(jobs):
            record = run_job(sep, job, index, bed_mode, provider, meta)
            failed += 0 if record["ok"] else 1
            out.write(json.dumps(record) + "\n")
            out.flush()
            print(f"[{index + 1}/{len(jobs)}] {job['id']} {'ok' if record['ok'] else 'FAILED: ' + str(record['error'])}"
                  f"  {record['wall_ms'] / 1000.0:.1f}s", file=sys.stderr)
    return 2 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
