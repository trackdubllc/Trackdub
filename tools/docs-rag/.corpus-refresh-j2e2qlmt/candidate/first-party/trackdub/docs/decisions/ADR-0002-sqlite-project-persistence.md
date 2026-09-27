# ADR-0002: SQLite project persistence

- Status: Draft
- Date: 2026-04-19

## Context

Trackdub is built around durable, inspectable pipeline artifacts. Users need to reopen a project and understand:

- what media the project points at
- which stages ran
- which model/provider/settings produced each artifact
- which edits superseded earlier machine output

That state is structured, relational, and local to a single project. At the same time, the workload also produces large binaries such as source media, extracted audio, stems, TTS takes, preview renders, and exports.

SQLite's current documentation still fits this shape well:

- it is a serverless, file-based, ACID database
- WAL mode is available when concurrent readers and writers matter
- SQLite explicitly documents the tradeoff between storing large blobs internally versus keeping them as external files

## Decision

Each `.trackdub` project will own a project-local SQLite database named `trackdub.db` for structured state, while the filesystem will own large media and generated artifacts.

More specifically:

- `trackdub.db` stores project metadata, stage runs, speakers, transcript revisions, translation revisions, voice assignments, TTS take metadata, mix plans, exports, consent records, and artifact metadata.
- Source media, extracted audio, stems, preview renders, final exports, and other large binaries live under the project folder's `media/` and `artifacts/` directories.
- Artifact rows store project-relative paths or storage keys plus hashes and media metadata, not absolute paths and not raw binary payloads.
- Machine-local state that is not part of one project, such as model cache inventory, benchmark history, settings, and logs, lives outside the project under a machine-local app data root.
- `src/Trackdub.Infrastructure` owns SQLite connection management, migrations, transactions, repository implementations, and path resolution.
- `src/Trackdub.Domain` may reason about artifact identity and provenance, but not about absolute filesystem locations.
- A thin mapper such as Dapper is acceptable inside Infrastructure, but the architectural decision is SQLite plus explicit SQL ownership, not a heavyweight ORM.

## Consequences

Positive:

- A project remains portable because its structured state travels with the project folder.
- Backups, copies, and bug-report bundles stay straightforward.
- SQLite fits the single-user local desktop model without introducing a service dependency.
- Large media files remain streamable and inspectable on disk without bloating the database.

Negative:

- Migrations and integrity checks become part of the app lifecycle.
- The app needs a reliable path-resolution layer for project-relative artifact locations.
- Careless direct file deletion can still orphan artifact records unless cleanup rules are enforced.

## Alternatives considered

### Store everything as JSON files

Rejected because stage history, artifact provenance, revisions, and downstream invalidation are relational enough that ad hoc JSON files would create harder consistency problems.

### Store large media blobs inside SQLite

Rejected for the default design because the project already has a natural artifact directory structure and the app benefits from keeping heavy binaries as normal files.

### Use one global database for all projects

Rejected because it weakens project portability and makes export, backup, and support workflows more fragile.

### Use a client/server database

Rejected because Trackdub is local-first and should not require a separate database service for its primary workflow.

## References

- [SQLite documentation index](https://www.sqlite.org/docs.html)
- [SQLite PRAGMA reference](https://www.sqlite.org/pragma.html)
- [SQLite WAL overview](https://www.sqlite.org/wal.html)
