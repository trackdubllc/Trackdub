#!/usr/bin/env python3
"""Optional golden-fixture generator for Spleeter STFT/mask parity tests.

Parity target: k2-fsa/sherpa-onnx scripts/spleeter/separate_onnx.py
(NOT full Deezer TensorFlow Spleeter predict path).

STFT contract locked by C# tests:
  n_fft=4096, hop=1024, win=4096, center=False, periodic Hann
  keep first 1024 bins
  time pad: padding = 512 - (num_frames % 512) when > 0
    (exact multiples still receive another 512-frame block — sherpa rule)
  sample rate 44100

Usage (optional; C# unit tests do not require these files):
  python generate_spleeter_reference.py
Writes sine/noise wav + npz next to this script.

Requires: numpy only (no kaldi-native_fbank / ONNX for the basic fixtures).
"""

from __future__ import annotations

import struct
from pathlib import Path

import numpy as np

SR = 44100
NFFT = 4096
HOP = 1024
MAX_FREQS = 1024
PAD_TO = 512


def periodic_hann(n: int) -> np.ndarray:
    i = np.arange(n, dtype=np.float64)
    return (0.5 * (1.0 - np.cos(2.0 * np.pi * i / n))).astype(np.float32)


def pad_time_frames(base_frames: int) -> int:
    """Match sherpa-onnx separate_onnx.py and SpleeterModelConstants.PadTimeFrames."""
    if base_frames <= 0:
        return PAD_TO
    remainder = base_frames % PAD_TO
    padding = PAD_TO - remainder
    return base_frames + padding if padding > 0 else base_frames


def stft_mag_phase(x: np.ndarray) -> tuple[np.ndarray, np.ndarray, int]:
    base = 1 + (x.shape[0] - NFFT) // HOP if x.shape[0] >= NFFT else 1
    target = pad_time_frames(base)
    win = periodic_hann(NFFT)
    mag = np.zeros((target, MAX_FREQS), dtype=np.float32)
    phase = np.zeros((target, MAX_FREQS), dtype=np.float32)
    for frame in range(target):
        start = frame * HOP
        buf = np.zeros(NFFT, dtype=np.float64)
        seg = x[start : start + NFFT]
        buf[: seg.shape[0]] = seg
        windowed = buf * win
        spec = np.fft.fft(windowed)
        mag[frame] = np.abs(spec[:MAX_FREQS]).astype(np.float32)
        phase[frame] = np.angle(spec[:MAX_FREQS]).astype(np.float32)
    return mag, phase, target


def soft_mask(v: np.ndarray, a: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    eps = np.float32(1e-10)
    denom = v * v + a * a + eps
    return v * v / denom, a * a / denom


def write_wav_pcm16_mono(path: Path, samples: np.ndarray, sample_rate: int = SR) -> None:
    """Write a one-channel PCM16 WAV. Filename must say mono when data is mono."""
    data = np.clip(samples, -1.0, 1.0)
    pcm = (data * 32767.0).astype("<i2")
    raw = pcm.tobytes()
    with path.open("wb") as f:
        f.write(b"RIFF")
        f.write(struct.pack("<I", 36 + len(raw)))
        f.write(b"WAVEfmt ")
        f.write(struct.pack("<IHHIIHH", 16, 1, 1, sample_rate, sample_rate * 2, 2, 16))
        f.write(b"data")
        f.write(struct.pack("<I", len(raw)))
        f.write(raw)


def main() -> None:
    out = Path(__file__).resolve().parent
    t = np.arange(SR * 2, dtype=np.float64) / SR
    sine = (0.5 * np.sin(2 * np.pi * 440 * t)).astype(np.float32)
    noise = (0.1 * np.random.default_rng(0).standard_normal(sine.shape[0])).astype(np.float32)

    mag, phase, target = stft_mag_phase(sine)
    np.savez_compressed(
        out / "spleeter_stft_sine_440.npz",
        sine=sine,
        mag=mag,
        phase=phase,
        target_frames=np.int32(target),
        nfft=np.int32(NFFT),
        hop=np.int32(HOP),
        max_freqs=np.int32(MAX_FREQS),
        pad_to=np.int32(PAD_TO),
    )
    write_wav_pcm16_mono(out / "sine_440_mono_2s_44k.wav", sine)
    write_wav_pcm16_mono(out / "noise_burst_mono_2s_44k.wav", noise)

    v = np.float32(0.3)
    a = np.float32(0.7)
    mv, ma = soft_mask(np.array([v]), np.array([a]))
    base = 1 + (sine.shape[0] - NFFT) // HOP
    print("mask production-style", float(mv[0]), float(ma[0]), "sum", float(mv[0] + ma[0]))
    print("wrote fixtures to", out)
    print(f"sine frames base={base} target={target} pad_rule=sherpa")


if __name__ == "__main__":
    main()
