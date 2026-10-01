# Separation evaluation corpus tooling

Design and rubric: `docs/audits/separation-eval-corpus-and-rubric.md` (gated repo).

`ingest.py` turns a hand-curated list of candidate items into a verified item manifest. It is stdlib-only (it uses `ffprobe` when on PATH, else `wave` for WAV files).

- Rejects any item whose license is not on the allow-list, is noncommercial or no-derivatives, or is share-alike unless `--allow-sharealike` is passed.
- Downloads over https only, into a cache directory that is **not** committed, and verifies any expected `sha256`.
- Rejects duplicate ids and byte-identical files.
- Assigns `dev` or `test` by hashing the source `group` (speaker, track, SFX pack), so a group never spans both splits.
- `verify` re-checks the manifest hash, split assignment, licenses and cached file hashes.

```bash
python tools/separation-eval/ingest.py ingest --items items.jsonl --cache D:/corpus-cache --out items.manifest.json
python tools/separation-eval/ingest.py verify --manifest items.manifest.json --cache D:/corpus-cache
python -m unittest discover -s tools/separation-eval -p 'test_*.py'
```

The licenses in `items.example.jsonl` are placeholders. Verify each item's license at its `license_evidence_url` before adding it.

## Python environment

The corpus generator, audio metrics, and evaluation runner use pinned NumPy and SciPy dependencies.
From a clean checkout, create an environment and install them before running those tools:

```bash
python -m venv .venv
# Windows PowerShell: .venv\Scripts\Activate.ps1
# macOS/Linux: source .venv/bin/activate
python -m pip install -r tools/separation-eval/requirements.txt
```

`ingest.py` and `recipe_coverage.py` only need the Python standard library when no cached RIR audio is
being measured. The full test suite and all DSP generation require the pinned environment above.

## Mixture generator

`mixgen.py` renders Tier A clips (mixture, dialogue reference, bed reference; 48 kHz float32 WAV plus `meta.json`) from a verified item manifest, for recipes A1 to A12 in the design doc.

```bash
python tools/separation-eval/mixgen.py recipes
python tools/separation-eval/mixgen.py generate --manifest items.manifest.json --cache D:/corpus-cache --out D:/corpus-dev --split dev --seed 1234
```

- Refuses to run unless the item manifest verifies against the cache.
- Each clip depends only on `(seed, clip id)`, so any subset regenerates identically.
- Uses only items from the requested split. A recipe fails loudly if the corpus lacks the tagged sources it needs; it never relaxes the tags.
- Item tags the recipes rely on (set `tags` in the items file): `instrumental`, `vocals` (music); `loud` (sfx); `crowd` (ambience); `whisper` (dialogue). RIRs are filtered to RT60 0.4 to 2.0 s.
- Dialogue-to-bed SNR is calibrated on dialogue-active frames, then the mixture is normalized to -23 LUFS with a joint peak guard applied to all three signals, so `mixture == dialogue + bed` exactly (before any codec round trip, which affects the mixture only).
- Reverberant dialogue (A3) keeps the reverb in the dialogue reference; it is what the separator must remove.
- A11 renders the refs and mixture in alternate formats: mono, 8 kHz, 16 kHz, and 5.1 (dialogue on the center channel).
- `--no-codec` skips the AAC/AC-3 round trip; otherwise 25% of clips (except A11) get one, which requires ffmpeg.
- A12 (20 min) holds several full-length float arrays in memory; expect a few GB of RAM.
- `audiomath.py` (loudness, activity mask, RT60, loading) is shared with the upcoming metric library.

## Runner and Spleeter baseline

`run_eval.py` scores a candidate on a corpus from `mixgen.py`. The candidate runs out of process. For Spleeter the command is `Trackdub.Benchmarks separation-eval` (`src/Trackdub.Benchmarks/SeparationEvalRunner.cs`), which drives the real `SpleeterStemSeparationEngine` through the headless composition root in one warm host and records wall time, RTF and peak working set per job.

```bash
python tools/separation-eval/run_eval.py --corpus D:/corpus-dev --work D:/eval-work --out spleeter-dev.json \
    --hardware-label "<machine description>" --provider cpu --model-cache-directory <Trackdub model-cache root>
```

The model-cache root must contain `model-cache-records.json` with a registered Spleeter model whose root contains
`vocals.onnx` and `accompaniment.onnx`. If `--model-cache-directory` is omitted, `--model-directory` is used as the
fallback model-cache root; neither option registers an arbitrary two-file directory as a Spleeter model. For direct
local weights, the runner also honors `TRACKDUB_SPLEETER_ONNX_PATH` when it points to that two-file directory.

- Inputs are prepared the way the pipeline feeds Spleeter: stereo (5.1 is downmixed), 44.1 kHz, PCM16. The engine writes mono 44.1 kHz stems.
- Two domains are scored. The reconstruction gate runs in the separator domain (44.1 kHz mono: does `vocals + bed` equal the mono input?). Bed leakage, bed damage and dialogue SI-SDR run in the reference domain (stems resampled to the clip rate and tiled to stereo), so the stereo-to-mono collapse and resampling are part of what is measured.
- The reconstruction gate checks the residual against the fixed threshold. When a candidate declares a band limit (`--band-limit-hz`, 11025 for Spleeter), only residual energy inside that band is judged; above-band residual is reported through `bandwidth_retained_db` and does not change pass/fail. Without a declared limit, the gate uses the full band. If there is no measurable reference energy inside a declared band, the gate uses the full-band mixture energy as the -60 dB reference floor. A silent mixture must produce silence inside the judged band. Stem precision/rounding (`--output-bits`, `--output-rounding`) only record `quantization_allowance_db` as a diagnostic, next to `in_band_residual_db`.
- `worst_window_leakage_db` is the worst 1 s window of the dialogue-relative leakage (projected error energy over reference-dialogue energy in that window). It stays bounded when the reference bed is near silent, and it is reported even when the whole-clip bed-relative figure is not applicable. Results schema version 3 changed its definition; do not compare it with version 2 results.
- Undefined metrics (for example bed leakage when no dialogue exists) are recorded as skipped, not as failures. A failed or missing job fails only its own clip.
- The results JSON records the corpus manifest hash, hardware label, per-clip metrics, per-recipe median and worst-decile aggregates, and cold versus warm RTF. It also records `model_provenance`: every model root the engine actually executed (the `TRACKDUB_SPLEETER_ONNX_PATH` override can supersede the planned model) with the sha256 of its `vocals.onnx` and `accompaniment.onnx`, compared against the pinned revision hashes in `bundled-models.manifest.json`, so results are attributable to the weights that produced them.
- The weights under test must be the pinned revision in `bundled-models.manifest.json`; check their sha256 before trusting a baseline.
- Known failure, Spleeter dev baseline: `a11-dev-002` (16 kHz variant) fails the reconstruction gate at -51.7 dB in-band. The Spleeter engine writes exactly zero for the first samples of every clip (24 samples, 0.54 ms, on most clips; 43 on this one), and this mixture starts mid-signal, so 96.5% of its residual falls in the first 10 ms. Excluding the first 50 ms it would pass at -64.5 dB. The gate is deliberately not relaxed for a start edge, because the shipped pipeline loses the same samples. Three other dev clips miss by under 2 dB. `a7-dev-004` and `a10-dev-001` are also start-edge failures: with the first 50 ms excluded their in-band residual is -61.2 and -64.9 dB. `a4-dev-002` is a quiet mixture (RMS 0.024): its modeled PCM16 truncation floor is about -62.6 dB, only 2.6 dB under the gate, and the rest of its residual is about as large, giving -59.6 dB in total; excluding the start barely changes it (-60.0 dB). Truncation alone would fail the gate only below about 0.018 RMS (about -35 dBFS), which real quiet dialogue can reach; the quantization allowance is reported but deliberately does not relax the gate.
- Known failure, Spleeter test baseline (172 clips, `c7bb929f`): 20 clips (11.6%) fail the in-band gate, between -49.8 and -59.9 dB; 8 of them miss by under 1.3 dB. 18 are writer edges. Excluding the first and last 50 ms, their in-band residual is -61.1 to -72.1 dB, and no edge-excluded clip stays above -60 dB. For 15 of them 99% of the start-edge energy lies within 21 to 59 samples of the start; `a1-test-011`, `a1-test-020`, `a2-test-001` and `a2-test-016` run longer (33, 35, 170 and 59 samples), and the first 50 ms holds 22% (`a1-test-011`) to 99% (`a10-test-000`) of the residual. `a2-test-003` is an end-edge failure (75% of its residual in the last 50 ms; -59.0 dB, -64.3 dB with both ends trimmed), and `a1-test-006` has both (15% at the end). The other two, `a5-test-005` and `a5-test-015` (-59.9 and -59.8 dB), have no edge: they are quiet mixtures (about -32.5 dBFS RMS) with a modeled truncation floor of about -56.5 dB, the `a4-dev-002` pattern. The gate stays strict, so these remain failures of the incumbent, not separation faults. Edge shares are exact time-domain energy fractions of the full residual; the scorer scales that energy by one global in-band fraction, so the edge share carries over to the in-band number.

## Finding and listing real items

`discover.py` builds the candidate list from `corpus-sources.v1.json` without downloading any audio. It queries Openverse (Freesound, Jamendo) and archive.org's LibriVox collection, keeps only items whose license maps onto the `ingest.py` allow-list (CC0, public domain, CC BY 3.0 or 4.0), caps items per creator, and writes `items.v1.jsonl`. API responses are cached, because Openverse's anonymous quota is 200 requests a day.

```bash
python tools/separation-eval/discover.py --spec tools/separation-eval/corpus-sources.v1.json --out tools/separation-eval/items.v1.jsonl --api-cache D:/api-cache
python tools/separation-eval/recipe_coverage.py --items tools/separation-eval/items.v1.jsonl --cache D:/corpus-cache
```

`recipe_coverage.py` checks every recipe in both splits and exits non-zero if a source category is thin,
the RIR cache is missing, or no cached RIR has an estimated RT60 in A3's 0.4–2.0 second range.

Caveats that affect what the corpus can show:
- Freesound items are 128 kbps MP3 previews (lossy, band-limited near 16 to 19 kHz); LibriVox is 64 kbps read speech truncated to `range_bytes`. Neither is film-grade audio, so above-band retention is only partly exercised.
- Music, SFX, ambience and whisper tags come from the search query (`tag_basis: search-query`). Listen to a sample of each tag before trusting the A2, A4, A5 and A8 strata.
- Impulse responses are checked for RT60 (0.4 to 2.0 s) from the audio cache before recipe coverage passes; mixgen applies the same range when generating A3.
- CC BY items carry their attribution text in the manifest; keep it with any redistributed copy.
- The group is the creator (or the LibriVox book), so one voice or artist stays on one side of the split.
