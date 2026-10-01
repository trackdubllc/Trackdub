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
python -m unittest discover -s tools/separation-eval
```

The licenses in `items.example.jsonl` are placeholders. Verify each item's license at its `license_evidence_url` before adding it.

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
    --hardware-label "<machine description>" --provider cpu --model-directory <dir with vocals.onnx + accompaniment.onnx>
```

- Inputs are prepared the way the pipeline feeds Spleeter: stereo (5.1 is downmixed), 44.1 kHz, PCM16. The engine writes mono 44.1 kHz stems.
- Two domains are scored. The reconstruction gate runs in the separator domain (44.1 kHz mono: does `vocals + bed` equal the mono input?). Bed leakage, bed damage and dialogue SI-SDR run in the reference domain (stems resampled to the clip rate and tiled to stereo), so the stereo-to-mono collapse and resampling are part of what is measured.
- The reconstruction gate uses each candidate's declared profile (`CANDIDATE_PROFILES` in `run_eval.py`): the band it processes (Spleeter 11025 Hz) and its stem precision and rounding (PCM16, truncating). The residual is judged inside the declared band; content kept above it is reported as `bandwidth_retained_db`, not gated. The allowed residual rises to the format's rounding noise on quiet mixtures. Override with `--band-limit-hz`, `--output-bits`, `--output-rounding`.
- Undefined metrics (for example bed leakage when no dialogue exists) are recorded as skipped, not as failures. A failed or missing job fails only its own clip.
- The results JSON records the corpus manifest hash, hardware label, per-clip metrics, per-recipe median and worst-decile aggregates, and cold versus warm RTF.
- The weights under test must be the pinned revision in `bundled-models.manifest.json`; check their sha256 before trusting a baseline.

## Finding and listing real items

`discover.py` builds the candidate list from `corpus-sources.v1.json` without downloading any audio. It queries Openverse (Freesound, Jamendo) and archive.org's LibriVox collection, keeps only items whose license maps onto the `ingest.py` allow-list (CC0, public domain, CC BY 3.0 or 4.0), caps items per creator, and writes `items.v1.jsonl`. API responses are cached, because Openverse's anonymous quota is 200 requests a day.

```bash
python tools/separation-eval/discover.py --spec tools/separation-eval/corpus-sources.v1.json --out tools/separation-eval/items.v1.jsonl --api-cache D:/api-cache
python tools/separation-eval/coverage.py --items tools/separation-eval/items.v1.jsonl
```

`coverage.py` checks every mixgen recipe against the list in both splits and exits non-zero if a source category is thin.

Caveats that affect what the corpus can show:
- Freesound items are 128 kbps MP3 previews (lossy, band-limited near 16 to 19 kHz); LibriVox is 64 kbps read speech truncated to `range_bytes`. Neither is film-grade audio, so full-band behaviour is only partly exercised.
- Music, SFX, ambience and whisper tags come from the search query (`tag_basis: search-query`). Listen to a sample of each tag before trusting the A2, A4, A5 and A8 strata.
- Impulse responses are checked for RT60 (0.4 to 2.0 s) only after download, when mixgen generates A3.
- CC BY items carry their attribution text in the manifest; keep it with any redistributed copy.
- The group is the creator (or the LibriVox book), so one voice or artist stays on one side of the split.
