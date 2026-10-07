# Atomic translation revisions and generated takes

## Acceptance and SQLite concurrency

`AtomicRevisionCommitBoundary` owns final acceptance of staged pipeline output.
The streaming collector retains every identity and payload in arrival order, including
malformed items. The boundary checks run, snapshot, source revision, stage, sequence,
segment identity, ordering and completeness before artifact preparation or output
degradation records. Streaming remains opt-in.

`SqliteAtomicRevisionPersistence` prepares and fingerprints temporary artifact bytes
outside the write transaction. A provisional revision number allows serialization;
the authoritative number is allocated and checked under the write lock. Inference,
alignment, audio postprocessing and stretching also happen outside that lock.

Publication uses a dedicated connection and
`BeginTransaction(IsolationLevel.Serializable, deferred: false)` (`BEGIN IMMEDIATE`).
It acquires SQLite's single writer reservation **before reading either current revision
head**. Other writers cannot change those heads between validation and commit, even
in WAL mode. Independent readers still see the previous committed snapshot.

A revision captures its expected source transcript ID and expected previous translation
ID for the project and normalized target language; null means no previous translation.
Both heads are checked under the lock. The revision insert also has an expected-head
condition and must insert exactly one row. Revision number allocation, segments and
word rows, artifact metadata and stale marking use transaction-aware SQL helpers on
that same connection. They open no nested repository transaction.

Each generated take validates its input before inference and again under its publication
write lock: the expected translation and source are still current; the persisted segment
belongs to that translation and project; its index, ordinal text, timing and source hash
match the synthesis input. The application additionally requires a completed, usable
take with the matching text hash and artifact ID. Artifact metadata and the take are
inserted together. Conflicts fail without retry against newer inputs. Write acquisition
uses the existing finite Microsoft.Data.Sqlite timeout.

Translation completion follows publication, manifest target-language handoff and
existing provenance updates. Takes may commit individually; stage completion follows
the existing stage/provenance policy, including partial success reporting.

## Artifact ownership, publication and cleanup

New translation and take paths include their output GUID. `IArtifactStore.CommitNewAsync`
uses create-only promotion and fails on an existing destination. It never overwrites.
A receipt records attempt ID, temporary path, final path, expected checksum, and whether
that attempt successfully created the destination. Successful promotion records ownership
immediately; observing a file already on disk does not establish ownership.

Consumers recognize publication through committed database artifact references. Directory
presence or scanning is not publication. Temporary files belong to the attempt; existing,
reused and shared/content-addressed files do not. The boundary writes metadata, promotes
the owned file and commits while holding the writer reservation.

Before confirmed commit, cancellation and failures trigger explicit rollback with
`CancellationToken.None`. After disposing the original connection/transaction, recovery
opens a fresh connection and takes another write reservation. It checks durable output
and artifact IDs. Confirmed publication preserves the files and returns success, even if
commit acknowledgement was lost or cancellation arrived afterward. Confirmed rollback
permits deletion only when the receipt records ownership, the file checksum still matches,
and no committed artifact reference uses its path. Legacy path separators are normalized
for that reference check. The check and deletion share the recovery write transaction.

Inconsistent metadata or an unavailable recovery database reports unresolved publication
and preserves final files. Attempt-owned temporary files are disposed independently of
the canceled token. A process crash can leave unreferenced files; consumers ignore them.
Recovery never sweeps unrelated files or deletes shared files by directory membership.

## Stale-take policy

The previous persisted translation is loaded under the write lock, scoped to project
and target language. Segments are matched by index. An index changes when added or
removed, ordinal translated text differs, start/end timing differs, or `SourceSegmentHash`
differs (including null versus non-null). A new segment GUID alone is not a change.

Only takes attributable through translated-segment links in that same language lineage
are marked stale for changed indices. Rows and files are preserved. Unchanged indices
retain take IDs, stale flags, stretch metadata and historical segment links; already stale
takes stay stale and usable takes remain available to the existing playback projection.
Unlinked takes and other target languages are neither invalidated nor adopted by index.

## Readiness selection intent

`RuntimeModelSelections.SelectionIntents` captures Automatic/Explicit per stage before
planning and participates in readiness cache identity. Factory-created defaults and Auto
settings are Automatic. `StudioSettings.Default.AutomaticModelAliases` records generated
default aliases (including the shipped Madlad translation default), and survives settings
serialization. Caller/project aliases, saved stage aliases, non-Auto configured overrides
without default provenance, and applied starter-pack stage pins are Explicit. A configured
alias with no provenance is Explicit for compatibility.

The desktop saves a deliberate non-Auto model choice as a stage alias, including choosing
the shipped default again. Choosing Auto removes that stage pin. Loading or saving settings
without changing a model does not create a pin; reloading discards pending choice provenance.

Default provenance is authoritative only while the generated alias matches the selection
and there is no caller/saved pin. Callers deliberately configuring that same default must
use an explicit alias or remove the automatic provenance. Provider overrides alone do
not affect model intent. Application substitutions and planner fallbacks preserve the
originating classification; caller aliases overlaid on host defaults become Explicit.
Resolved aliases and `RequirePreferredModelAlias` do not classify the request.

`StageReadiness.ResolveAction` remains `string?`. DownloadRequired maps to `bundle-needed`
for Automatic and `download` for Explicit requests. Checksum repair always maps to
`download`; other actions retain their existing meanings. The desktop remediation is:

> Run trackdub models bundle-needed to list required models, then download the missing models.

## Evidence

`AtomicRevisionCommitBoundaryTests` exercises production streaming and TTS handlers,
the real application boundary, SQLite repositories and filesystem store. A forwarding
recorder proves two valid stream items followed by a wrong-revision item reach the actual
boundary. It compares reopened durable rows, stale flags and output files after rejection.
Independent SQLite connections and deterministic barriers exercise writer exclusion and
earlier-writer conflicts. Additional cases cover stream failure/cancellation, inference-time
head changes, SQL and promotion failures, collisions, commit ambiguity, unavailable recovery,
owned/shared cleanup, and changed/unchanged/cross-language stale policy.

`ReadinessResolveActionTests` exercises classification and fallback through `EvaluateAsync`,
including cache separation, caller overlays, applied pack pins and integrity repair.
The gated `EnvironmentReadinessItemTests` checks the desktop remediation text;
`SettingsReadinessRenderingTests` opens the real Settings window and checks its visible,
laid-out Environment remediation. `ModelSelectionIntentSettingsTests` covers deliberate
desktop choices, Auto, unchanged defaults and reloads. SDK readiness and check-command
tests verify the emitted `bundle-needed` action using the production readiness service.

SQLite references: [transaction locking](https://sqlite.org/lang_transaction.html),
[Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).
