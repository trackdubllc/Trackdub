---
kind: error_handling
name: Structured Exception Types and CLI Error Reporting
category: error_handling
scope:
    - '**'
source_files:
    - src/Trackdub.Sdk/ErrorCode.cs
    - src/Trackdub.Cli/CliErrorReporter.cs
    - src/Trackdub.Contracts/Licensing/ModelNotAvailableException.cs
    - src/Trackdub.Sdk/ProjectLockedException.cs
    - src/Trackdub.Infrastructure/Persistence/Sqlite/ProjectDatabaseException.cs
    - src/Trackdub.Infrastructure/Components/OpenVinoComponentException.cs
    - src/Trackdub.Licensing/FingerprintException.cs
    - src/Trackdub.Application/Transcripts/FutureStageContracts.cs
    - src/Trackdub.Contracts/Pipeline/IConsentService.cs
---

Trackdub uses a layered .NET exception strategy combining domain-specific exceptions, SDK error codes, and structured CLI error output. There is no centralized error-handling framework; instead, conventions are enforced through dedicated exception types and a single CLI reporter.

**System/approach**
- Domain and infrastructure layers throw strongly-typed `Exception` subclasses to signal recoverable vs. fatal conditions (e.g., missing models, locked projects, database corruption).
- The Sdk layer defines a shared `ErrorCode` enum that annotates user-facing errors with machine-parseable identifiers.
- The CLI surface serializes all errors to stderr as single-line JSON via a central `CliErrorReporter`, enabling programmatic consumption by callers or automation.
- Application-layer workflows use `InvalidOperationException`, `ArgumentNullException`, `ArgumentException`, and `FileNotFoundException` for argument validation and state errors, following standard .NET conventions.

**Key files and packages**
- `src/Trackdub.Sdk/ErrorCode.cs` — canonical error-code enum used across CLI reporting and some exceptions.
- `src/Trackdub.Cli/CliErrorReporter.cs` — static helper that writes `CliErrorObject` (code, message, optional parameter/artifact paths) to stderr as JSON.
- `src/Trackdub.Contracts/Licensing/ModelNotAvailableException.cs` — `RequiredModelNotAvailableException` indicating missing ONNX models and whether auto-download is possible.
- `src/Trackdub.Sdk/ProjectLockedException.cs` — concurrency conflict exception carrying `ErrorCode.ProjectLocked`.
- `src/Trackdub.Infrastructure/Persistence/Sqlite/ProjectDatabaseException.cs` — base + derived exceptions (`ProjectDatabaseSchemaVersionException`, `ProjectDatabaseCorruptedException`) with recovery guidance and backup paths.
- `src/Trackdub.Infrastructure/Components/OpenVinoComponentException.cs` — wrapper for OpenVINO component failures.
- `src/Trackdub.Licensing/FingerprintException.cs` — platform-agnostic wrapper around hardware fingerprint failures.
- `src/Trackdub.Application/Transcripts/FutureStageContracts.cs` — `ExportStageException` wrapping an `ExportFailureReport` for export-stage failures.
- `src/Trackdub.Contracts/Pipeline/IConsentService.cs` — `ConsentRequiredException`, `TtsReferenceTextRequiredException` for pipeline consent/state gates.

**Architecture and conventions**
- Argument validation uses `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` at method entry points throughout the codebase.
- Business-state violations (concurrent runs, tier-gated exports, missing project records) throw `InvalidOperationException` with descriptive messages.
- Resource-not-found scenarios throw `FileNotFoundException` with the offending path.
- Recoverable operational failures (missing model, runtime not ready, stage prerequisite missing) throw domain-specific exceptions so callers can decide retry/download/fallback behavior.
- The CLI never prints raw stack traces; it funnels every error through `CliErrorReporter.ReportValidationError`, `ReportStageFailure`, or `ReportError`, producing a stable JSON schema consumed by scripts and tests.
- Pipeline stages wrap lower-level failures in richer exceptions (e.g., `ExportStageException` carries a structured `ExportFailureReport`), keeping low-level details from leaking into the application boundary.

**Conventions and constraints**
- All user-visible error codes flow through the `ErrorCode` enum; new codes must be added there before being emitted by `CliErrorReporter`.
- Exceptions that carry structured data (model id/path, report objects, backup paths) expose those fields as properties rather than embedding them in the message alone.
- Database startup failures always include a `BackupPath` when available, guiding recovery.
- No `try/catch(Exception)` swallows are used outside of cancellation handling (`OperationCanceledException`) and best-effort process cleanup; unhandled exceptions propagate to the top-level CLI handler.