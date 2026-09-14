---
kind: logging_system
name: Trackdub Application Logging System
category: logging_system
scope:
    - '**'
source_files:
    - src/Trackdub.Contracts/IApplicationLogger.cs
    - src/Trackdub.Infrastructure/Logging/RollingFileApplicationLogger.cs
    - src/Trackdub.Infrastructure/Logging/DebugApplicationLogger.cs
    - src/Trackdub.Application/Logging/ApplicationLoggerExtensions.cs
    - src/Trackdub.Cli/CliLoggingBootstrap.cs
    - src/Trackdub.Cli/CliLoggingConfiguration.cs
    - src/Trackdub.Cli/StderrApplicationLogger.cs
    - src/Trackdub.Composition/CompositionRoot.cs
---

Trackdub implements a layered logging system built around a custom `IApplicationLogger` interface, with multiple concrete implementations selected via dependency injection at composition time. The system separates the logging contract from infrastructure concerns and provides both file-based and debug output sinks.

**Framework and Architecture**
The core abstraction is `IApplicationLogger` defined in `Trackdub.Contracts`, exposing `LogDebug`, `LogInformation`, `LogWarning`, `LogError`, plus crash-safe `LogErrorSynchronously` and flush methods (`Flush`, `Flush(TimeSpan)`). This interface intentionally avoids coupling to any specific logging framework, allowing different sinks for different execution contexts.

Two primary implementations exist in `Trackdub.Infrastructure.Logging`:
- `RollingFileApplicationLogger`: Production sink that writes structured log entries to a bounded rolling file under the user's app data directory. It uses an asynchronous writer task with a `BlockingCollection<string>` queue (max 1024 entries), supports configurable maximum file size (default 1MB), archive rotation (default 3 archives, session mode uses 10), per-entry truncation (64KB default), and thread-safe operations with `Interlocked` counters for enqueued/settled/written metrics. Log entries include ISO timestamps, process ID, thread ID, level tags, and optional exception traces.
- `DebugApplicationLogger`: Development sink using `System.Diagnostics.Debug.WriteLine` for Visual Studio/IDE output during debugging sessions.

**CLI-Specific Logging**
The CLI has its own lightweight logging setup in `Trackdub.Cli`. `CliLoggingConfiguration.CreateLoggerFactory` returns either a `StderrLoggerProvider` (when verbose) or a `NoopLogger` (silent mode), writing to `Console.Error` with timestamped `[LEVEL]` formatted lines. `StderrApplicationLogger` bridges this to the `IApplicationLogger` contract for early startup paths before the full DI container is ready.

**Composition and Registration**
In `CompositionRoot.cs`, `IApplicationLogger` is registered as a singleton bound to `RollingFileApplicationLogger` with `SessionMaxArchiveFiles` (10), `ApplicationLogLevel.Information` minimum level, and `rotateOnStartup: true`. This ensures each application session gets a fresh log file while preserving up to 10 previous session archives.

**Structured Logging Conventions**
`ApplicationLoggerExtensions` in `Trackdub.Application.Logging` provides template-based logging helpers that accept message templates with `{placeholder}` syntax and format arguments at write time using a compiled regex. This keeps message templates constant while deferring formatting costs until needed.

**Usage Patterns**
Services accept `IApplicationLogger?` as an optional constructor parameter and fall back to `new DebugApplicationLogger()` when not provided by DI. This pattern appears across `FileSystemArtifactStore`, `Sha256FileFingerprintService`, `HuggingFaceModelDownloader`, `ModelDownloaderAdapter`, and `SqliteConsentService`, ensuring logging works even outside the full composition context.

**Log Level Strategy**
The system uses four levels: `Debug`, `Information`, `Warning`, `Error`. The production logger defaults to `Information` minimum level, meaning debug logs are filtered out in normal operation. Error-level logs have synchronous write paths via `LogErrorSynchronously` for crash scenarios where async flushing might not complete.