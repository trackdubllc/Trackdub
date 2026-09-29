# JSON source generation: startup-latency measurement (2026-09-29)

## Question

The change that moved the startup-path stores onto source-generated JSON metadata (PR #305) initially
described itself as a startup-latency win, without a measurement behind it. This note records the
measurement, and the reason the latency wording was removed from the code.

## Scope

The changed read+deserialize expressions:

| Store | Type deserialized from generated metadata |
| --- | --- |
| `NvidiaAfxRuntimeManifestLoader` | `NvidiaAfxRuntimeManifest` |
| `TrtRtxEpBundleManifestLoader` | `TrtRtxEpBundleManifestDto` |
| `TrackdubStoragePathResolver` | `StorageConfig` |
| `FileSmokeVerdictStore` | `SmokeVerdictFilePayload` |
| `LocalModelCacheRecordStore` | `LocalModelCacheRecord[]` |
| `WinNativeDepsManifestLoader` | `WinNativeDepsManifestRoot` |

## Method

- Temporary probe classes reproduced each expression twice: once with the pre-change reflection
  options (including the AFX loader's per-call `new JsonSerializerOptions`), once with the generated
  metadata. File I/O, existence guards and candidate probing were identical on both sides; loader
  validation and post-processing ran in neither arm, because the change did not touch them. The
  media shape called the real loader against a faithful pre-change copy of its body.
- `dotnet test -c Release --no-build`, one shape per fresh process. This matters: touching
  `InfrastructureSerializationContext.Default` builds metadata for all four declared types, so a
  second shape measured in the same process would no longer be a first call.
- The timed region is the first read. The same call then ran 200 times and the median is recorded as
  the steady-state number.
- Both modes first warmed the generic serializer entry points with a throwaway dummy type, so the
  timed call measures metadata resolution rather than first-call JIT of the generic path.
- The two modes alternated within each repetition, and the whole pairing was repeated with the order
  reversed to rule out ordering bias.
- Raw data: one JSONL row per run (`TRACKDUB_JSON_PROBE_MODE`, `TRACKDUB_JSON_PROBE_OUT`),
  aggregated as the median per (shape, mode). The probe classes were committed as `0a6eb24e` and
  removed again in the follow-up to #305; they are not part of CI.

Environment: AMD Ryzen 7 5700X3D / 64 GB / Windows 11 Pro 26200 / .NET SDK 10.0.401, `main` @
`1fd52a3a`.

## Results

Five changed stores read once each, in a fresh process (`startup.all`):

| Metadata path | n | median | min-max |
| --- | --- | --- | --- |
| reflection (pre-change) | 17 | 76.0 ms | 59.0-104.0 ms |
| generated | 16 | 89.6 ms | 69.0-167.6 ms |

Paired by repetition, generated was slower in 16/16 pairs (median delta +20.1 ms), in both
orderings.

Single store per process:

| Shape | reflection | generated |
| --- | --- | --- |
| win-native-deps manifest (real loader vs pre-change body) | 8.10 ms (n=9) | 9.41 ms (n=9), slower 9/9 |

Steady state after the first read (median of 200): 0.10-0.35 ms per read in both modes. Raw
file-read floor for an 895-character manifest: 0.56 ms cold, 0.14 ms steady.

Diagnostic with the generated contexts built before the timed region:

| Shape | reflection | generated |
| --- | --- | --- |
| five stores | 39.7 ms (n=5) | 24.0 ms (n=5) |
| win-native-deps manifest | 7.62 ms (n=3) | 2.87 ms (n=3) |

## Interpretation

- The difference is entirely one-time metadata/type-info initialization. Once metadata exists, both
  metadata sources deserialize at the same speed.
- Timed cold — the scenario the latency claim described — the generated-metadata reads are not
  faster; they are consistently slower. The most plausible cause is that touching
  `InfrastructureSerializationContext.Default` builds metadata eagerly for all four declared types,
  while the pre-change stores built one type's metadata lazily on first use. The prewarmed
  diagnostic is consistent with this: with metadata already built, the generated path is the faster
  one.
- Source-generated metadata remains a legitimate goal (compile-time metadata instead of runtime
  reflection/emit). It is simply not a measured startup-latency win for these stores, and nothing
  in the code should claim that it is.

## Limits

- The harness is a fresh test host, so the magnitudes are dominated by cold JIT and runtime setup
  that a shipped app pays once, in a different order. Only the relative comparison is meaningful.
- One machine, one SDK, small fixtures. These numbers are not a benchmark gate; re-running them
  means restoring the probe classes from git and repeating the protocol above.
