---
kind: configuration_system
name: Trackdub Studio Settings System (JSON + Environment Variables)
category: configuration_system
scope:
    - '**'
source_files:
    - src/Trackdub.Contracts/IStudioSettingsService.cs
    - src/Trackdub.Infrastructure/Settings/JsonStudioSettingsService.cs
    - src/Trackdub.Application/Settings/DeviceAffinitySettings.cs
    - .env.example
    - src/Trackdub.Composition/CompositionRoot.cs
    - src/Trackdub.Sdk/TrackdubConfig.cs
    - src/Trackdub.Cli/CliLoggingConfiguration.cs
---

Trackdub uses a hybrid configuration system combining JSON persistence for user settings with environment variables for runtime and deployment-time configuration.

## Core Architecture

**Primary settings model**: `StudioSettings` record in `Trackdub.Contracts/IStudioSettingsService.cs` defines the canonical configuration shape with ~40 properties covering language defaults, model overrides (ASR, translation, TTS, separation), export/playback preferences, hardware policies, theme, and license acceptance flags. A static `Default` instance provides sensible defaults.

**Persistence layer**: `JsonStudioSettingsService` in `Trackdub.Infrastructure/Settings/` implements `IStudioSettingsService`, serializing/deserializing to a single `settings.json` file using `System.Text.Json` with camelCase naming and custom tolerant converters for enum fields that accept both string keys and numeric values.

**Storage location**: Determined by `TrackdubStoragePaths` — typically under platform-specific app data directories (e.g., `%LOCALAPPDATA%\Trackdub\settings.json`).

## Configuration Sources & Layering

1. **Environment variables** (highest priority at startup): `.env.example` documents ASP.NET Core (`ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS`), logging (`LOGGING_LEVEL`, `LOG_OUTPUT_PATH`), model caching (`MODELS_CACHE_DIR`, `MODEL_DOWNLOAD_TIMEOUT_SECONDS`, `AUTO_DOWNLOAD_MODELS`), .NET runtime tuning (`DOTNET_TieredCompilation*`), SQLite path (`DATABASE_PATH`), and optional API/auth/rate-limiting placeholders.

2. **JSON settings file** (`settings.json`): User-editable persistent configuration loaded via `IStudioSettingsService.LoadAsync()`. Corrupt files are archived as `settings.json.{timestamp}.corrupt` and defaults are used.

3. **Programmatic defaults**: `StudioSettings.Default` provides fallback values when no persisted settings exist.

## CLI & SDK Integration

- **CLI**: `CliLoggingConfiguration` configures logging based on verbose flag; commands like `ConfigCommand` emit paths and persisted settings summaries.
- **SDK**: `TrackdubConfig` reads settings directly from disk for read-only snapshots, filtering out non-existent project paths.
- **Composition**: `CompositionRoot.cs` registers `IStudioSettingsService` as singleton and wires it into GPU runtime bootstrapping, model download orchestration, and update services.

## Specialized Settings

- **Device affinity**: `DeviceAffinitySettings` in `Trackdub.Application/Settings/` persists per-stage device pinning to `device-affinity.json` with atomic writes via temp files.
- **Model override keys**: `ModelVariantOverrideKeys` builds composite keys (`stage:alias`) for fine-grained model variant selection.
- **Theme normalization**: `AppThemeNames.Normalize()` ensures forward compatibility with unknown theme strings.

## Conventions & Constraints

- All enum-based settings use string-keyed serialization via dedicated `*Settings` helper classes (`AsrModelOverrideSettings`, `TranslationModelOverrideSettings`, etc.) supporting both legacy numeric and modern string formats.
- Dictionary settings use case-insensitive comparers for stage/model alias keys.
- Recent projects list is capped at 10 entries and deduplicated by normalized path.
- All file I/O uses atomic write patterns (write to `.tmp`, then `File.Move`) to prevent corruption.
- Unknown or invalid enum values normalize to safe defaults rather than throwing.