import json
import unittest

import numpy as np

import metrics

SR = 44100
LIMIT = 11025.0
LSB16 = 1.0 / 32768.0


def noise(rms, seconds=3.0, seed=0, channels=1):
    rng = np.random.default_rng(seed)
    return (rng.standard_normal((int(seconds * SR), channels)) * rms).astype(np.float32)


def lowpass(x, cutoff=LIMIT):
    spec = np.fft.rfft(x.astype(np.float64), axis=0)
    spec[np.fft.rfftfreq(x.shape[0], 1.0 / SR) > cutoff] = 0.0
    return np.fft.irfft(spec, n=x.shape[0], axis=0).astype(np.float32)


def quantise(x, bits=16):
    step = 2.0 ** -(bits - 1)
    return (np.round(x / step) * step).astype(np.float32)


def tone(freq, seconds=2.0):
    t = np.arange(int(seconds * SR)) / SR
    return np.sin(2 * np.pi * freq * t).astype(np.float32)[:, None] * 0.1


class BandFractionTests(unittest.TestCase):
    def test_tones_land_in_the_right_region(self):
        low = metrics.band_energy_fractions(tone(5000), SR, LIMIT)
        high = metrics.band_energy_fractions(tone(15000), SR, LIMIT)
        guard = metrics.band_energy_fractions(tone(LIMIT + 50), SR, LIMIT)
        self.assertGreater(low[0], 0.99)
        self.assertLess(low[1], 0.01)
        self.assertGreater(high[1], 0.99)
        self.assertLess(guard[0] + guard[1], 0.05)

    def test_silence_has_no_fractions(self):
        self.assertEqual(metrics.band_energy_fractions(np.zeros((SR, 1), np.float32), SR, LIMIT), (0.0, 0.0))

    def test_batching_does_not_change_the_result(self):
        x = noise(0.1, 2.0)
        whole = metrics.band_energy_fractions(x, SR, LIMIT)
        old = metrics.BAND_BATCH_FRAMES
        metrics.BAND_BATCH_FRAMES = 3
        try:
            batched = metrics.band_energy_fractions(x, SR, LIMIT)
        finally:
            metrics.BAND_BATCH_FRAMES = old
        self.assertAlmostEqual(whole[0], batched[0], places=9)
        self.assertAlmostEqual(whole[1], batched[1], places=9)


class BandLimitTests(unittest.TestCase):
    def setUp(self):
        self.mix = noise(0.1, seed=1)
        self.band_limited = lowpass(self.mix)
        self.zero = np.zeros_like(self.mix)

    def test_a_declared_band_judges_only_the_residual_inside_it(self):
        undeclared = metrics.check_reconstruction(self.mix, self.zero, self.band_limited, SR)
        self.assertFalse(undeclared.passed)
        self.assertAlmostEqual(undeclared.residual_db, -3.0, delta=0.5)

        declared = metrics.check_reconstruction(self.mix, self.zero, self.band_limited, SR, band_limit_hz=LIMIT)
        self.assertTrue(declared.passed, declared.reasons)
        self.assertAlmostEqual(declared.residual_db, undeclared.residual_db, delta=0.01)
        self.assertLess(declared.in_band_residual_db, -60.0)
        self.assertAlmostEqual(declared.mixture_above_band_db, -3.0, delta=0.6)
        self.assertLess(declared.bandwidth_retained_db, -30.0)
        self.assertEqual(declared.band_limit_hz, LIMIT)

    def test_an_error_inside_the_declared_band_still_fails(self):
        damaged = self.band_limited + lowpass(noise(0.01, seed=2))
        r = metrics.check_reconstruction(self.mix, self.zero, damaged, SR, band_limit_hz=LIMIT)
        self.assertFalse(r.passed)
        self.assertIn("in-band residual", r.reasons[0])
        self.assertGreater(r.in_band_residual_db, -40.0)

    def test_full_band_output_reports_full_retention(self):
        r = metrics.check_reconstruction(self.mix, 0.3 * self.mix, 0.7 * self.mix, SR, band_limit_hz=LIMIT)
        self.assertTrue(r.passed)
        self.assertGreater(r.bandwidth_retained_db, -0.5)

    def test_content_kept_above_the_band_is_reported_and_not_gated(self):
        r = metrics.check_reconstruction(self.mix, self.zero, self.band_limited + 0.5 * (self.mix - self.band_limited),
                                         SR, band_limit_hz=LIMIT)
        self.assertTrue(r.passed, r.reasons)
        self.assertAlmostEqual(r.bandwidth_retained_db, -6.0, delta=0.6)

    def test_no_declaration_keeps_the_original_semantics(self):
        r = metrics.check_reconstruction(self.mix, 0.3 * self.mix, 0.7 * self.mix, SR)
        self.assertEqual(r.in_band_residual_db, r.residual_db)
        self.assertIsNone(r.bandwidth_retained_db)
        self.assertIsNone(r.quantization_allowance_db)
        self.assertEqual(r.effective_threshold_db, metrics.RECONSTRUCTION_GATE_DB)

    def test_delay_is_still_diagnosed_inside_a_declared_band(self):
        late = np.roll(self.band_limited, 50, axis=0)
        r = metrics.check_reconstruction(self.mix, self.zero, late, SR, band_limit_hz=LIMIT)
        self.assertFalse(r.passed)
        self.assertEqual(r.lag_samples, 50)


class QuantisationTests(unittest.TestCase):
    def split_and_quantise(self, mix):
        return quantise(0.3 * mix), quantise(0.7 * mix)

    def test_quantization_allowance_is_diagnostic_and_does_not_relax_gate(self):
        mix = noise(0.002, seed=3)
        dialogue, bed = self.split_and_quantise(mix)
        strict = metrics.check_reconstruction(mix, dialogue, bed, SR)
        aware = metrics.check_reconstruction(mix, dialogue, bed, SR, output_bits=16)
        self.assertFalse(strict.passed)
        self.assertFalse(aware.passed, aware.reasons)
        self.assertGreater(aware.quantization_allowance_db, metrics.RECONSTRUCTION_GATE_DB)
        self.assertEqual(aware.effective_threshold_db, metrics.RECONSTRUCTION_GATE_DB)
        self.assertEqual(aware.residual_db, strict.residual_db)

    def test_loud_mixture_keeps_the_strict_threshold(self):
        mix = noise(0.1, seed=4)
        dialogue, bed = self.split_and_quantise(mix)
        r = metrics.check_reconstruction(mix, dialogue, bed, SR, output_bits=16)
        self.assertTrue(r.passed)
        self.assertEqual(r.effective_threshold_db, metrics.RECONSTRUCTION_GATE_DB)
        self.assertLess(r.quantization_allowance_db, metrics.RECONSTRUCTION_GATE_DB)

    def test_real_errors_are_not_hidden_by_the_allowance(self):
        mix = noise(0.1, seed=5)
        dialogue, bed = self.split_and_quantise(mix)
        err = noise(0.1 * 10 ** (-45 / 20), seed=6)
        r = metrics.check_reconstruction(mix, dialogue, bed + err, SR, output_bits=16)
        self.assertFalse(r.passed)

    def test_quiet_mixture_with_a_real_error_still_fails(self):
        mix = noise(0.002, seed=7)
        dialogue, bed = self.split_and_quantise(mix)
        r = metrics.check_reconstruction(mix, dialogue, bed + 0.3 * mix, SR, output_bits=16)
        self.assertFalse(r.passed)

    def test_silent_mixture_does_not_use_quantization_allowance(self):
        z = np.zeros((SR, 1), np.float32)
        self.assertTrue(metrics.check_reconstruction(z, z, z, SR, output_bits=16).passed)
        one_lsb = z + LSB16
        self.assertFalse(metrics.check_reconstruction(z, z, one_lsb, SR, output_bits=16).passed)

    def test_silent_mixture_ignores_output_above_declared_band(self):
        t = np.arange(SR, dtype=np.float64) / SR
        above_band = (0.01 * np.sin(2 * np.pi * 18_000 * t)).astype(np.float32)[:, None]
        z = np.zeros_like(above_band)
        result = metrics.check_reconstruction(z, z, above_band, SR, band_limit_hz=LIMIT)
        self.assertTrue(result.passed, result.reasons)

    def test_silent_mixture_still_gates_output_inside_declared_band(self):
        t = np.arange(SR, dtype=np.float64) / SR
        in_band = (0.01 * np.sin(2 * np.pi * 1_000 * t)).astype(np.float32)[:, None]
        z = np.zeros_like(in_band)
        result = metrics.check_reconstruction(z, z, in_band, SR, band_limit_hz=LIMIT)
        self.assertFalse(result.passed)
        self.assertIn("judged band", result.reasons[0])

    def test_declared_band_with_no_reference_energy_only_gates_residual_in_band(self):
        t = np.arange(SR, dtype=np.float64) / SR
        mix = (0.01 * np.sin(2 * np.pi * 18_000 * t)).astype(np.float32)[:, None]
        z = np.zeros_like(mix)
        result = metrics.check_reconstruction(mix, z, z, SR, band_limit_hz=LIMIT)
        self.assertTrue(result.passed, result.reasons)
        self.assertIsNone(result.in_band_residual_db)

    def test_quiet_quantised_mixture_still_fails_with_declarations(self):
        mix = noise(0.002, seed=8)
        limited = lowpass(mix)
        dialogue, bed = quantise(0.3 * limited), quantise(0.7 * limited)
        strict = metrics.check_reconstruction(mix, dialogue, bed, SR)
        declared = metrics.check_reconstruction(mix, dialogue, bed, SR, band_limit_hz=LIMIT, output_bits=16)
        self.assertFalse(strict.passed)
        self.assertFalse(declared.passed, declared.reasons)
        self.assertAlmostEqual(declared.residual_db, strict.residual_db, delta=0.01)
        self.assertLess(declared.in_band_residual_db, declared.residual_db)

    def test_truncating_writers_need_the_truncate_model(self):
        mix = noise(0.002, seed=10)
        dialogue, bed = np.trunc(0.3 * mix / LSB16) * LSB16, np.trunc(0.7 * mix / LSB16) * LSB16
        as_rounded = metrics.check_reconstruction(mix, dialogue, bed, SR, output_bits=16)
        as_truncated = metrics.check_reconstruction(mix, dialogue, bed, SR, output_bits=16, output_rounding="truncate")
        self.assertFalse(as_rounded.passed)
        self.assertFalse(as_truncated.passed, as_truncated.reasons)
        self.assertAlmostEqual(as_truncated.quantization_allowance_db - as_rounded.quantization_allowance_db,
                               10 * np.log10(4.0), delta=0.01)
        self.assertEqual(as_truncated.output_rounding, "truncate")
        self.assertEqual(as_truncated.effective_threshold_db, metrics.RECONSTRUCTION_GATE_DB)

    def test_unknown_rounding_is_rejected(self):
        x = noise(0.1)
        with self.assertRaises(metrics.MetricError):
            metrics.check_reconstruction(x, x, x, SR, output_bits=16, output_rounding="stochastic")

    def test_higher_precision_lowers_the_allowance(self):
        mix = noise(0.002, seed=9)
        a16 = metrics.check_reconstruction(mix, 0.3 * mix, 0.7 * mix, SR, output_bits=16).quantization_allowance_db
        a24 = metrics.check_reconstruction(mix, 0.3 * mix, 0.7 * mix, SR, output_bits=24).quantization_allowance_db
        self.assertAlmostEqual(a16 - a24, 20 * np.log10(256), delta=0.01)


class ValidationTests(unittest.TestCase):
    def test_invalid_declarations_raise(self):
        x = noise(0.1)
        with self.assertRaises(metrics.MetricError):
            metrics.check_reconstruction(x, x, x, SR, band_limit_hz=SR / 2)
        with self.assertRaises(metrics.MetricError):
            metrics.check_reconstruction(x, x, x, SR, band_limit_hz=0.0)
        with self.assertRaises(metrics.MetricError):
            metrics.check_reconstruction(x, x, x, SR, output_bits=1)

    def test_structural_failures_carry_the_declaration(self):
        x = noise(0.1)
        r = metrics.check_reconstruction(x, x[:-1], x, SR, band_limit_hz=LIMIT, output_bits=16)
        self.assertFalse(r.passed)
        self.assertEqual((r.band_limit_hz, r.output_bits), (LIMIT, 16))

    def test_results_serialise(self):
        x = noise(0.1)
        json.dumps(metrics.check_reconstruction(x, 0.5 * x, 0.5 * x, SR, band_limit_hz=LIMIT, output_bits=16).as_dict())


if __name__ == "__main__":
    unittest.main()
