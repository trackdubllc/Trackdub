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
- `dotnet test -c Release --no-build`, one shape per fresh process, so each shape's first read is
  measured with cold serializer machinery and JIT. The generated context's per-type metadata is
  lazy (see Interpretation below): a fresh process isolates the one-time options-instance setup
  and JIT warming, not an eager multi-type metadata build.
- The timed region is the first read. The same call then ran 200 times and the median is recorded as
  the steady-state number.
- Both modes first warmed the generic serializer entry points with a throwaway dummy type, so the
  timed call measures metadata resolution rather than first-call JIT of the generic path.
- The two modes alternated within each repetition, and the whole pairing was repeated with the order
  reversed to rule out ordering bias.
- Test project: `tests/Trackdub.Infrastructure.Tests` (five stores) and
  `tests/Trackdub.Media.Tests` (win-native-deps single store).
- Command for the five-store comparison: `dotnet test -c Release --filter
  "FullyQualifiedName~JsonLatencyProbe.Probe_startup_all_stores"` with
  `TRACKDUB_JSON_PROBE_MODE=reflection` or `TRACKDUB_JSON_PROBE_MODE=generated` (default). Run one
  mode per fresh process. The single-store tests use `Probe_afx`, `Probe_trtrtx`,
  `Probe_storageconfig`, `Probe_smokeverdict`, and `Probe_modelcache` in the Infrastructure project;
  `Probe_winnd` is in the Media project.
- The probe emitted one JSONL row per run to `TRACKDUB_JSON_PROBE_OUT` (or a temporary default
  path), and the rows were aggregated as the median per (shape, mode). The original JSONL files
  were not retained as a repository or CI artifact, so the reported raw rows are unavailable for
  independent inspection. The probe revision and commands below allow the measurements to be
  regenerated; preserve the output at a durable location when rerunning.
- The probe classes were committed as `0a6eb24e` (`JsonLatencyProbe` in
  `tests/Trackdub.Infrastructure.Tests/JsonLatencyProbe.cs` and
  `tests/Trackdub.Media.Tests/JsonLatencyProbe.cs`) and removed again in this follow-up to #305;
  they are not part of CI. To reproduce, restore them from git at that revision.
- The probe at `0a6eb24e` includes an aggregate `Probe_startup_all_stores_prewarmed_serializers`
  test. The separate per-phase probe source used for the table below was temporary and was not
  retained, so those phase rows have no replay command and are recorded diagnostics rather than
  independently reproducible measurements.

Environment: AMD Ryzen 7 5700X3D / 64 GB / Windows 11 Pro 26200 / .NET SDK 10.0.401, `main` @
`1fd52a3a`.

## Results

Five changed stores read once each, in a fresh process (`startup.all`):

| Metadata path | n | median | min-max |
| --- | --- | --- | --- |
| reflection (pre-change) | 17 | 76.0 ms | 59.0-104.0 ms |
| generated | 16 | 89.6 ms | 69.0-167.6 ms |

Paired by repetition, generated was slower in 16/16 pairs (median paired delta +20.1 ms, generated minus reflection), in both
orderings.

Single store per process:

| Shape | reflection | generated |
| --- | --- | --- |
| win-native-deps manifest (real loader vs pre-change body) | 8.10 ms (n=9) | 9.41 ms (n=9), slower 9/9 |

Steady state after the first read (median of 200): 0.10-0.35 ms per read in both modes. Raw
file-read floor for an 895-character manifest: 0.56 ms cold, 0.14 ms steady.

Prewarm diagnostic — metadata for the two heaviest graphs (storage config and the model cache index,
~51 ms of the cold total) built before the timed region, so this measures what is left once those
costs are already paid:

| Shape | reflection | generated |
| --- | --- | --- |
| five stores | 39.7 ms (n=5) | 24.0 ms (n=5) |
| win-native-deps manifest | 7.62 ms (n=3) | 2.87 ms (n=3) |

## Where the cold cost actually lives

Per phase, in a fresh process, in order (median of 5-6 runs). "Warm machinery" repeats 200 steady
reads of each phase before moving to the next; "cold chain" runs the phases back to back with no
steady loops, mirroring the `startup.all` protocol.

| Phase | generated, warm machinery | reflection, warm machinery | generated, cold chain | reflection, cold chain |
| --- | --- | --- | --- | --- |
| `InfrastructureSerializationContext.Default` | 1.72 ms | n/a | 0.58 ms | n/a |
| second `Default` access | 0.05 ms | n/a | — | — |
| AFX manifest | 11.61 ms | 15.86 ms | 9.18 ms | 10.17 ms |
| TRT-RTX manifest | 5.82 ms | 5.64 ms | 5.24 ms | 3.44 ms |
| storage config | 9.87 ms | 9.13 ms | 9.34 ms | 5.69 ms |
| smoke verdicts | 2.98 ms | 2.76 ms | 1.45 ms | 1.27 ms |
| model cache index | 41.20 ms | 42.07 ms | — | — |
| **sum** | **73.25 ms** | **75.46 ms** | **25.79 ms** | **20.57 ms** |

## Interpretation

The generated contract metadata is **already lazy per type**, so there is no eager build to remove:

- The generated context stores each type as `_X ??= (JsonTypeInfo<X>)Options.GetTypeInfo(typeof(X))`
  (`obj/…/generated/…/InfrastructureSerializationContext.<Type>.g.cs`), and the phase data confirms
  it: every store's first read carries its own cost. The last phase, smoke verdicts, still costs
  1.45 ms (generated) / 1.27 ms (reflection) after three heavier reads, whereas a second access to
  the already-built context costs 0.05 ms.
- The only eager work when the context is first touched is the context's own options instance:
  0.58-1.72 ms, once.
- Splitting the shared context into per-store contexts therefore cannot remove any measured cost: it
  would keep the same per-type metadata work and add one options instance per extra context. With a
  shared context, a startup that reads one store already builds metadata for that store only.

Where the two metadata sources differ, they trade places rather than one dominating: AFX favours the
generated path (9-12 ms vs 10-16 ms), TRT-RTX and storage config favour reflection (5.2/9.3 ms vs
3.4/5.7 ms in the cold chain), and the model cache index — 41 ms of the ~75 ms — is the same in both.
With the serializer machinery warm between phases the sums are equal within noise (73.3 ms vs
75.5 ms); back to back cold they are not (25.8 ms vs 20.6 ms over four stores). The `startup.all`
result above is that same back-to-back protocol, so its sign is consistent with cold-JIT volume
rather than with eager metadata.

Conclusion for the latency question: source-generated metadata is not a startup-latency win for these
stores, and no lazy-metadata fix is available to make it one — the per-type laziness that would be
asked for is already how the runtime behaves. If startup latency on this path is the goal, the lever
is the model cache index read (41 ms of ~75 ms, identical in both metadata sources), not the
metadata source.

The prewarm diagnostic above also corrects its own earlier reading: it looked like evidence of an
eager four-type build, but it prewarmed the two heaviest graphs, which is what actually disappeared
from the timed region — not four types' worth of metadata.

## Limits

- The harness is a fresh test host, so the magnitudes are dominated by cold JIT and runtime setup
  that a shipped app pays once, in a different order. Only the relative comparison is meaningful.
- One machine, one SDK, small fixtures. These numbers are not a benchmark gate; re-running them
  means restoring the probe classes from git and repeating the protocol above.
