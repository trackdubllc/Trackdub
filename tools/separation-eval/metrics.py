"""Ground-truth separation metrics for the Trackdub evaluation corpus.

Definitions follow docs/audits/separation-eval-corpus-and-rubric.md (section 4.1).
All signals are float arrays shaped (samples, channels) at the same sample rate and
must already be sample-aligned; length mismatches are errors, never trimmed.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np
from scipy import linalg, ndimage, signal

import audiomath as am

DB_FLOOR = -120.0
DEFAULT_MAX_LAG_MS = 2.0
CORRELATION_BLOCK = 1 << 19
RIDGE = 1e-8
DB_CEIL = 120.0
GUARD_FRAMES = 3
LSD_EPS = 1e-10
LSD_BATCH_FRAMES = 2048
DEFAULT_WINDOW_SECONDS = 1.0
MIN_ACTIVE_WINDOW_FRACTION = 0.25


class MetricError(Exception):
    pass


@dataclass(frozen=True)
class LeakageResult:
    """Dialogue-correlated error in the estimated bed, measured on dialogue-active frames.

    leakage_to_bed_db: energy of the dialogue-explained part of the estimated bed, relative to
        estimated-bed energy. Lower is better. None when the estimated bed is silent.
    dialogue_residual_db: the same leaked energy relative to the reference dialogue energy
        (how much of the dialogue survives in the bed). Lower is better.
    worst_window_leakage_db: worst analysis window of leakage_to_bed_db; None when no window qualifies.
    """
    leakage_to_bed_db: float | None
    dialogue_residual_db: float
    worst_window_leakage_db: float | None
    active_fraction: float
    max_lag_samples: int
    window_seconds: float

    def as_dict(self) -> dict:
        return {
            "leakage_to_bed_db": self.leakage_to_bed_db,
            "dialogue_residual_db": self.dialogue_residual_db,
            "worst_window_leakage_db": self.worst_window_leakage_db,
            "active_fraction": self.active_fraction,
            "max_lag_samples": self.max_lag_samples,
            "window_seconds": self.window_seconds,
        }


def _db(numerator: float, denominator: float) -> float:
    return max(DB_FLOOR, 10.0 * math.log10(max(numerator, 1e-30) / denominator))


def _lag_indices(max_lag: int, nfft: int) -> np.ndarray:
    return np.arange(-max_lag, max_lag + 1) % nfft


def _correlations(d: np.ndarray, e: np.ndarray, k: int) -> tuple[np.ndarray, np.ndarray]:
    """Accumulate r[i, j, tau] = sum d_i[u] d_j[u+tau] for |tau| <= 2k and
    q[i, c, a] = sum d_i[u] e_c[u+a] for |a| <= k, in blocks."""
    n, ch = d.shape
    r = np.zeros((ch, ch, 4 * k + 1))
    q = np.zeros((ch, ch, 2 * k + 1))
    for start in range(0, n, CORRELATION_BLOCK):
        db = d[start:start + CORRELATION_BLOCK].astype(np.float64)
        eb = e[start:start + CORRELATION_BLOCK].astype(np.float64)
        nfft = 1 << int(math.ceil(math.log2(2 * db.shape[0] + 4 * k + 2)))
        spec_d = np.fft.rfft(db, nfft, axis=0)
        spec_e = np.fft.rfft(eb, nfft, axis=0)
        idx_r, idx_q = _lag_indices(2 * k, nfft), _lag_indices(k, nfft)
        for i in range(ch):
            for j in range(ch):
                r[i, j] += np.fft.irfft(spec_d[:, j] * np.conj(spec_d[:, i]), nfft)[idx_r]
                q[i, j] += np.fft.irfft(spec_e[:, j] * np.conj(spec_d[:, i]), nfft)[idx_q]
    return r, q


def project_onto_dialogue(d: np.ndarray, e: np.ndarray, max_lag: int) -> np.ndarray:
    """Least-squares projection of each channel of `e` onto delayed copies of every channel of `d`
    (lags -max_lag..max_lag). Returns the dialogue-explained part of `e`."""
    n, ch = d.shape
    k = max_lag
    lags = np.arange(-k, k + 1)
    r, q = _correlations(d, e, k)
    size = ch * (2 * k + 1)
    gram = np.empty((size, size))
    tau = np.subtract.outer(lags, lags) + 2 * k
    for i in range(ch):
        for j in range(ch):
            gram[i * (2 * k + 1):(i + 1) * (2 * k + 1), j * (2 * k + 1):(j + 1) * (2 * k + 1)] = r[i, j][tau]
    rhs = q.transpose(0, 2, 1).reshape(size, ch)
    trace_mean = float(np.trace(gram)) / size
    if trace_mean <= 0.0:
        return np.zeros_like(e, dtype=np.float64)
    gram[np.diag_indices(size)] += RIDGE * trace_mean
    h = linalg.solve(gram, rhs, assume_a="pos")
    est = np.zeros((n, ch))
    for c in range(ch):
        for i in range(ch):
            taps = h[i * (2 * k + 1):(i + 1) * (2 * k + 1), c]
            est[:, c] += signal.fftconvolve(d[:, i].astype(np.float64), taps)[k:k + n]
    return est


def _validate_triplet(ref_dialogue: np.ndarray, ref_bed: np.ndarray, est_bed: np.ndarray) -> None:
    for name, a in (("ref_dialogue", ref_dialogue), ("ref_bed", ref_bed), ("est_bed", est_bed)):
        if a.ndim != 2:
            raise MetricError(f"{name} must be shaped (samples, channels)")
        if not np.isfinite(a).all():
            raise MetricError(f"{name} contains NaN or infinite samples")
    if not (ref_dialogue.shape == ref_bed.shape == est_bed.shape):
        raise MetricError(
            f"shape mismatch: dialogue {ref_dialogue.shape}, ref bed {ref_bed.shape}, est bed {est_bed.shape}")


def _activity_masks(ref_dialogue: np.ndarray, sr: int) -> tuple[np.ndarray, np.ndarray]:
    """Sample-level (active, inactive) masks from the reference dialogue. The inactive mask is
    guarded by GUARD_FRAMES on each side so reverb tails and onsets are not treated as untouched bed."""
    mask = am.frame_activity(ref_dialogue, sr)
    if not mask.any():
        raise MetricError("reference dialogue has no active frames")
    frame = max(1, int(sr * 20 / 1000))
    n = ref_dialogue.shape[0]
    active = np.zeros(n, dtype=bool)
    inactive = np.zeros(n, dtype=bool)
    covered = len(mask) * frame
    active[:covered] = np.repeat(mask, frame)
    guarded = ndimage.binary_dilation(mask, iterations=GUARD_FRAMES)
    inactive[:covered] = np.repeat(~guarded, frame)
    return active, inactive


def _window_bounds(n: int, win: int) -> list[tuple[int, int]]:
    bounds = [(lo, min(lo + win, n)) for lo in range(0, n, win)]
    if len(bounds) > 1 and bounds[-1][1] - bounds[-1][0] < win // 2:
        last = bounds.pop()
        bounds[-1] = (bounds[-1][0], last[1])
    return bounds


def bed_leakage(ref_dialogue: np.ndarray, ref_bed: np.ndarray, est_bed: np.ndarray, sr: int,
                max_lag_ms: float = DEFAULT_MAX_LAG_MS,
                window_s: float = DEFAULT_WINDOW_SECONDS) -> LeakageResult:
    """Measure how much reference dialogue remains in the estimated bed.

    The estimated bed is projected onto the reference dialogue with a short multichannel FIR
    (tolerating small delays and filtering), fitted independently in each `window_s` window so
    leakage that comes and goes is not averaged away. The primary ratio is dialogue-explained
    estimated-bed energy divided by estimated-bed energy, both summed over dialogue-active frames.
    The second ratio uses reference-dialogue energy as its denominator.

    Known limits: leakage whose gain changes within a window is under-measured (by up to about
    3 dB for a burst covering half a window), and the fit has a chance-capture floor of roughly
    10*log10(unknowns / samples per window) dB relative to estimated-bed energy (about -21 dB at
    the defaults), so very small dialogue components approach a nonzero capture floor.
    """
    _validate_triplet(ref_dialogue, ref_bed, est_bed)
    max_lag = max(1, int(max_lag_ms * sr / 1000.0))
    win = int(window_s * sr)
    if win < 4 * max_lag:
        raise MetricError("window_s is too short for max_lag_ms")

    sample_mask, _ = _activity_masks(ref_dialogue, sr)

    bounds = _window_bounds(ref_dialogue.shape[0], win)
    leak = np.zeros_like(est_bed, dtype=np.float64)
    for lo, hi in bounds:
        if sample_mask[lo:hi].any():
            leak[lo:hi] = project_onto_dialogue(ref_dialogue[lo:hi], est_bed[lo:hi], max_lag)

    leak_e = float((leak[sample_mask] ** 2).sum())
    bed_e = float((est_bed[sample_mask].astype(np.float64) ** 2).sum())
    dlg_e = float((ref_dialogue[sample_mask].astype(np.float64) ** 2).sum())

    worst: float | None = None
    if bed_e > 0.0:
        for lo, hi in bounds:
            m = sample_mask[lo:hi]
            if m.sum() < MIN_ACTIVE_WINDOW_FRACTION * (hi - lo):
                continue
            w_bed = float((est_bed[lo:hi][m].astype(np.float64) ** 2).sum())
            if w_bed <= 0.0:
                continue
            value = _db(float((leak[lo:hi][m] ** 2).sum()), w_bed)
            worst = value if worst is None else max(worst, value)

    return LeakageResult(
        leakage_to_bed_db=_db(leak_e, bed_e) if bed_e > 0.0 else None,
        dialogue_residual_db=_db(leak_e, dlg_e),
        worst_window_leakage_db=worst,
        active_fraction=float(sample_mask.mean()),
        max_lag_samples=max_lag,
        window_seconds=window_s,
    )


@dataclass(frozen=True)
class BedDamageResult:
    """How far the estimated bed is from the reference bed, beyond dialogue leakage.

    si_sdr_db: scale-invariant SDR of the whole bed, higher is better.
    si_sdr_active_db / si_sdr_inactive_db: the same on dialogue-active and (guarded) dialogue-inactive
        samples; the inactive figure isolates damage, since no dialogue is present to confuse it.
    lsd_inactive_db: mean log-spectral distance on frames entirely inside dialogue-inactive regions,
        where the bed should be untouched; 0 is identical, lower is better.
    Fields are None when undefined (silent reference, or no inactive region).
    """
    si_sdr_db: float | None
    si_sdr_active_db: float | None
    si_sdr_inactive_db: float | None
    lsd_inactive_db: float | None
    inactive_fraction: float
    lsd_frames: int

    def as_dict(self) -> dict:
        return {
            "si_sdr_db": self.si_sdr_db,
            "si_sdr_active_db": self.si_sdr_active_db,
            "si_sdr_inactive_db": self.si_sdr_inactive_db,
            "lsd_inactive_db": self.lsd_inactive_db,
            "inactive_fraction": self.inactive_fraction,
            "lsd_frames": self.lsd_frames,
        }


def si_sdr(ref: np.ndarray, est: np.ndarray) -> float | None:
    """Scale-invariant SDR over all samples and channels jointly. None when the reference is silent.
    A silent or orthogonal estimate scores DB_FLOOR; an exact match scores DB_CEIL."""
    r = ref.astype(np.float64).reshape(-1)
    e = est.astype(np.float64).reshape(-1)
    ref_energy = float(r @ r)
    if ref_energy <= 0.0:
        return None
    target = (float(e @ r) / ref_energy) * r
    noise = e - target
    t_energy, n_energy = float(target @ target), float(noise @ noise)
    if t_energy <= 0.0:
        return DB_FLOOR
    if n_energy <= 0.0:
        return DB_CEIL
    return float(np.clip(10.0 * math.log10(t_energy / n_energy), DB_FLOOR, DB_CEIL))


def _lsd_nfft(sr: int) -> int:
    return 1 << max(6, int(round(math.log2(0.0427 * sr))))


def log_spectral_distance(ref: np.ndarray, est: np.ndarray, inactive: np.ndarray, sr: int) -> tuple[float | None, int]:
    """Mean over channels and qualifying frames of the RMS (over bins, DC excluded) difference in
    dB between the log power spectra. Only frames wholly inside `inactive` are used.
    Returns (value or None, frames per channel)."""
    n, ch = ref.shape
    nfft = _lsd_nfft(sr)
    hop = nfft // 4
    if n < nfft:
        return None, 0
    starts = np.arange(0, n - nfft + 1, hop)
    active_count = np.concatenate([[0], np.cumsum(~inactive)])
    starts = starts[(active_count[starts + nfft] - active_count[starts]) == 0]
    if starts.size == 0:
        return None, 0
    window = signal.get_window("hann", nfft)
    offsets = np.arange(nfft)[None, :]
    total = 0.0
    for b in range(0, starts.size, LSD_BATCH_FRAMES):
        idx = starts[b:b + LSD_BATCH_FRAMES][:, None] + offsets
        for c in range(ch):
            lr = 10.0 * np.log10(np.abs(np.fft.rfft(ref[idx, c].astype(np.float64) * window, axis=1)) ** 2 + LSD_EPS)
            le = 10.0 * np.log10(np.abs(np.fft.rfft(est[idx, c].astype(np.float64) * window, axis=1)) ** 2 + LSD_EPS)
            total += float(np.sqrt(((lr - le)[:, 1:] ** 2).mean(axis=1)).sum())
    return total / (starts.size * ch), int(starts.size)


def bed_damage(ref_dialogue: np.ndarray, ref_bed: np.ndarray, est_bed: np.ndarray, sr: int) -> BedDamageResult:
    """Measure damage to the bed: SI-SDR overall and split by dialogue activity, plus log-spectral
    distance where no dialogue is present. Dialogue activity comes from the reference dialogue;
    very quiet dialogue (below the activity floor) counts as inactive, so whispers are a known blind spot."""
    _validate_triplet(ref_dialogue, ref_bed, est_bed)
    active, inactive = _activity_masks(ref_dialogue, sr)
    lsd, frames = log_spectral_distance(ref_bed, est_bed, inactive, sr)
    return BedDamageResult(
        si_sdr_db=si_sdr(ref_bed, est_bed),
        si_sdr_active_db=si_sdr(ref_bed[active], est_bed[active]) if active.any() else None,
        si_sdr_inactive_db=si_sdr(ref_bed[inactive], est_bed[inactive]) if inactive.any() else None,
        lsd_inactive_db=lsd,
        inactive_fraction=float(inactive.mean()),
        lsd_frames=frames,
    )


RECONSTRUCTION_GATE_DB = -60.0
LAG_SEARCH_SAMPLES = 4800
LAG_SEARCH_SECONDS = 10
BAND_GUARD_HZ = 250.0
BAND_FFT_SIZE = 8192
BAND_BATCH_FRAMES = 256
QUANTIZATION_MARGIN = 2.0
# Per-stem rounding error variance in LSB^2: rounding to nearest is uniform on +-0.5 LSB (1/12);
# truncation toward zero, as the shipped PCM16 writer does, is uniform on one LSB of the opposite sign (1/3).
ROUNDING_VARIANCE_LSB2 = {"nearest": 1.0 / 12.0, "truncate": 1.0 / 3.0}


@dataclass(frozen=True)
class ReconstructionResult:
    """Hard-gate check that a separator's outputs add back to its input.

    Without a declared band the gate compares the full-band `residual_db` with `threshold_db`. With a
    declared band limit it compares `in_band_residual_db` instead; content above the limit is reported
    as `bandwidth_retained_db` and never gated. Output precision never relaxes the gate.

    residual_db: full-band residual relative to the full-band mixture.
    in_band_residual_db: residual inside the declared band relative to the mixture's energy there; this
        is the gated value when a band is declared (equal to residual_db otherwise).
    effective_threshold_db: the threshold applied to the gated residual (equal to threshold_db).
    quantization_allowance_db: estimated rounding noise for `output_bits` output written with
        `output_rounding` ("nearest" or "truncate"), with a 2x margin, relative to the full-band
        mixture. It is a diagnostic only and does not relax the gate.
    bandwidth_retained_db: energy the outputs keep above the declared band relative to the mixture's
        energy there (0 = all kept, very negative = dropped). Reported, never gated.
    mixture_above_band_db: share of the mixture's energy above the declared band, in dB of the total.
    worst_channel_db / max_abs_residual / lag_samples: diagnostics; lag is estimated only on failure.
    None is used wherever a figure is undefined (shape mismatch, silent mixture, no band declared).
    """
    passed: bool
    residual_db: float | None
    in_band_residual_db: float | None
    effective_threshold_db: float | None
    quantization_allowance_db: float | None
    bandwidth_retained_db: float | None
    mixture_above_band_db: float | None
    band_limit_hz: float | None
    output_bits: int | None
    output_rounding: str
    worst_channel_db: float | None
    max_abs_residual: float | None
    lag_samples: int | None
    threshold_db: float
    reasons: tuple[str, ...]

    def as_dict(self) -> dict:
        return {
            "passed": self.passed,
            "residual_db": self.residual_db,
            "in_band_residual_db": self.in_band_residual_db,
            "effective_threshold_db": self.effective_threshold_db,
            "quantization_allowance_db": self.quantization_allowance_db,
            "bandwidth_retained_db": self.bandwidth_retained_db,
            "mixture_above_band_db": self.mixture_above_band_db,
            "band_limit_hz": self.band_limit_hz,
            "output_bits": self.output_bits,
            "output_rounding": self.output_rounding,
            "worst_channel_db": self.worst_channel_db,
            "max_abs_residual": self.max_abs_residual,
            "lag_samples": self.lag_samples,
            "threshold_db": self.threshold_db,
            "reasons": list(self.reasons),
        }


def _failed(reasons: list[str], threshold_db: float, band_limit_hz: float | None = None,
            output_bits: int | None = None, output_rounding: str = "nearest") -> ReconstructionResult:
    return ReconstructionResult(False, None, None, None, None, None, None, band_limit_hz, output_bits,
                                output_rounding, None, None, None, threshold_db, tuple(reasons))


def _estimate_lag(mixture: np.ndarray, summed: np.ndarray, sr: int) -> int | None:
    seg = min(mixture.shape[0], LAG_SEARCH_SECONDS * sr)
    x = mixture[:seg].astype(np.float64).mean(axis=1)
    y = summed[:seg].astype(np.float64).mean(axis=1)
    if not x.any() or not y.any():
        return None
    corr = signal.correlate(y, x, mode="full", method="fft")
    center = seg - 1
    window = corr[max(0, center - LAG_SEARCH_SAMPLES):center + LAG_SEARCH_SAMPLES + 1]
    return int(np.argmax(np.abs(window))) - min(center, LAG_SEARCH_SAMPLES)


def band_energy_fractions(x: np.ndarray, sr: int, band_limit_hz: float,
                          guard_hz: float = BAND_GUARD_HZ) -> tuple[float, float]:
    """Fractions of the signal's spectral energy below (band_limit - guard) and above (band_limit + guard),
    from Hann-windowed 50%-overlap frames summed over channels. The guard band is excluded from both.
    Frames lie wholly inside the signal, so its edges do not leak broadband energy into the band."""
    nfft = BAND_FFT_SIZE
    hop = nfft // 2
    freqs = np.fft.rfftfreq(nfft, 1.0 / sr)
    inside = freqs <= band_limit_hz - guard_hz
    outside = freqs >= band_limit_hz + guard_hz
    window = signal.get_window("hann", nfft)
    in_e = out_e = total = 0.0
    for c in range(x.shape[1]):
        padded = x[:, c].astype(np.float64)
        if padded.size < nfft:
            padded = np.concatenate([padded, np.zeros(nfft - padded.size)])
        starts = np.arange(0, padded.size - nfft + 1, hop)
        for b in range(0, starts.size, BAND_BATCH_FRAMES):
            idx = starts[b:b + BAND_BATCH_FRAMES][:, None] + np.arange(nfft)[None, :]
            power = np.abs(np.fft.rfft(padded[idx] * window, axis=1)) ** 2
            in_e += float(power[:, inside].sum())
            out_e += float(power[:, outside].sum())
            total += float(power.sum())
    if total <= 0.0:
        return 0.0, 0.0
    return in_e / total, out_e / total


def check_reconstruction(mixture: np.ndarray, est_dialogue: np.ndarray, est_bed: np.ndarray, sr: int,
                         threshold_db: float = RECONSTRUCTION_GATE_DB, band_limit_hz: float | None = None,
                         output_bits: int | None = None, output_rounding: str = "nearest") -> ReconstructionResult:
    """Gate: the dialogue and bed outputs must have exactly the mixture's shape and sum back to it.

    Without `band_limit_hz` the full-band residual is compared against `threshold_db`. With it, only the
    residual below the declared band limit is judged; the energy the separator keeps above the limit is
    reported as `bandwidth_retained_db` and never changes pass/fail. Output precision only adds the
    expected output-rounding noise as a diagnostic.
    Never raises for bad separator output; it fails the gate.
    """
    def fail(reasons: list[str]) -> ReconstructionResult:
        return _failed(reasons, threshold_db, band_limit_hz, output_bits, output_rounding)

    if band_limit_hz is not None and not BAND_GUARD_HZ < band_limit_hz < sr / 2.0 - BAND_GUARD_HZ:
        raise MetricError(
            f"band_limit_hz {band_limit_hz} must lie between {BAND_GUARD_HZ} and {sr / 2.0 - BAND_GUARD_HZ} Hz")
    if output_bits is not None and output_bits < 2:
        raise MetricError("output_bits must be at least 2")
    if output_rounding not in ROUNDING_VARIANCE_LSB2:
        raise MetricError(f"output_rounding must be one of {sorted(ROUNDING_VARIANCE_LSB2)}")
    for name, a in (("mixture", mixture), ("dialogue", est_dialogue), ("bed", est_bed)):
        if a.ndim != 2:
            return fail([f"{name} must be shaped (samples, channels)"])
    if not (mixture.shape == est_dialogue.shape == est_bed.shape):
        return fail([f"shape mismatch: mixture {mixture.shape}, dialogue {est_dialogue.shape}, "
                     f"bed {est_bed.shape}"])
    if not all(np.isfinite(a).all() for a in (mixture, est_dialogue, est_bed)):
        return fail(["output contains NaN or infinite samples"])

    mix = mixture.astype(np.float64)
    summed = est_dialogue.astype(np.float64) + est_bed.astype(np.float64)
    residual = mix - summed
    res_e, mix_e, sum_e = float((residual ** 2).sum()), float((mix ** 2).sum()), float((summed ** 2).sum())
    max_abs = float(np.abs(residual).max()) if residual.size else 0.0

    res_in, mix_in = res_e, mix_e
    retained: float | None = None
    above: float | None = None
    if band_limit_hz is not None:
        res_in = res_e * band_energy_fractions(residual, sr, band_limit_hz)[0]
        mix_frac_in, mix_frac_out = band_energy_fractions(mix, sr, band_limit_hz)
        mix_in = mix_e * mix_frac_in
        mix_out = mix_e * mix_frac_out
        if mix_out > 0.0:
            above = _db(mix_out, mix_e)
            retained = _db(sum_e * band_energy_fractions(summed, sr, band_limit_hz)[1], mix_out)

    allowance_energy = 0.0
    if output_bits is not None:
        lsb = 2.0 ** -(output_bits - 1)
        allowance_energy = (QUANTIZATION_MARGIN * residual.size * 2.0 * lsb * lsb
                            * ROUNDING_VARIANCE_LSB2[output_rounding])

    if mix_e <= 0.0:
        passed = res_e == 0.0
        return ReconstructionResult(
            passed, None, None, threshold_db, None, None, None, band_limit_hz, output_bits, output_rounding, None, max_abs, None,
            threshold_db, () if passed else ("mixture is silent but outputs are not",))

    threshold_energy = mix_e * 10.0 ** (threshold_db / 10.0)
    residual_db = _db(res_e, mix_e)
    in_band_db = _db(res_in, mix_in) if mix_in > 0.0 else None
    allowance_db = _db(allowance_energy, mix_e) if output_bits is not None else None
    effective_db = threshold_db

    reasons: list[str] = []
    if band_limit_hz is not None and mix_in > 0.0:
        if res_in > mix_in * 10.0 ** (threshold_db / 10.0):
            reasons.append(f"in-band residual {in_band_db:.1f} dB exceeds gate {effective_db:.1f} dB")
    elif res_e > threshold_energy:
        reasons.append(f"full-band residual {residual_db:.1f} dB exceeds gate {effective_db:.1f} dB")
    lag = _estimate_lag(mixture, summed, sr) if reasons else None
    if lag:
        reasons.append(f"outputs appear shifted by {lag} samples relative to the mixture")

    per_channel = [_db(float((residual[:, c] ** 2).sum()), max(float((mix[:, c] ** 2).sum()), 1e-30))
                   for c in range(mixture.shape[1])]
    return ReconstructionResult(
        not reasons, residual_db, in_band_db, effective_db, allowance_db, retained, above, band_limit_hz,
        output_bits, output_rounding, max(per_channel), max_abs, lag, threshold_db, tuple(reasons))
