# Media Import & Setup

<cite>
**Referenced Files in This Document**
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IFileSystemProbe.cs](file://src/Trackdub.Contracts/IFileSystemProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [TrackdubBuilder.cs](file://src/Trackdub.Sdk/TrackdubBuilder.cs)
- [TrackdubConfig.cs](file://src/Trackdub.Sdk/TrackdubConfig.cs)
- [TrackdubSession.cs](file://src/Trackdub.Sdk/TrackdubSession.cs)
- [TrackdubSessionFactory.cs](file://src/Trackdub.Sdk/TrackdubSessionFactory.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)
- [TrackdubProjectContext.cs](file://src/Trackdub.Sdk/TrackdubProjectContext.cs)
- [AudioPreparationTests.cs](file://tests/Trackdub.Application.Tests/AudioPreparationTests.cs)
- [SpeechAudioPreparationGuardrailTests.cs](file://tests/Trackdub.Application.Tests/SpeechAudioPreparationGuardrailTests.cs)
- [ProjectMediaIngestServiceTests.cs](file://tests/Trackdub.Application.Tests/ProjectMediaIngestServiceTests.cs)
</cite>

## Table of Contents
1. [Introduction](#introduction)
2. [Project Structure](#project-structure)
3. [Core Components](#core-components)
4. [Architecture Overview](#architecture-overview)
5. [Detailed Component Analysis](#detailed-component-analysis)
6. [Dependency Analysis](#dependency-analysis)
7. [Performance Considerations](#performance-considerations)
8. [Troubleshooting Guide](#troubleshooting-guide)
9. [Conclusion](#conclusion)

## Introduction
This document explains Trackdub’s media import and setup process for interactive dubbing workflows. It covers supported file formats, automatic media detection, quality assessment during import, the project creation wizard, metadata configuration, initial settings, audio extraction options, video format handling, preprocessing steps, workspace organization, and performance optimization for large files. It also provides guidance on common import issues, error recovery strategies, and best practices for organizing assets.

## Project Structure
The media import and setup flow spans several layers:
- CLI entry points and wizards for user-driven setup
- SDK orchestration for batch discovery, session/project context resolution, and processing pipelines
- Contracts that define interfaces for probing, extraction, fingerprinting, and settings
- Domain and application services that perform validation, preparation, and quality checks
- Tools for one-off ingestion tasks

```mermaid
graph TB
subgraph "CLI"
P["Program.cs"]
W["DubSetupWizard.cs"]
T["MediaIngestCommand.cs"]
end
subgraph "SDK"
BFD["BatchFileDiscovery.cs"]
BO["BatchOptions.cs"]
BP["BatchProcessor.cs"]
TP["TrackdubProjectPaths.cs"]
TB["TrackdubBuilder.cs"]
TC["TrackdubConfig.cs"]
TS["TrackdubSession.cs"]
TSS["TrackdubSessionFactory.cs"]
PCR["TrackdubProjectContextResolver.cs"]
PCX["TrackdubProjectContext.cs"]
end
subgraph "Contracts"
MP["IMediaProbe.cs"]
FSP["IFileSystemProbe.cs"]
AES["IAudioExtractionService.cs"]
FPS["IFileFingerprintService.cs"]
SSS["IStudioSettingsService.cs"]
end
P --> W
P --> T
T --> BFD
BFD --> BP
BP --> PCR
PCR --> PCX
BP --> TS
TS --> TSS
TSS --> TB
TB --> TC
BP --> MP
BP --> FSP
BP --> AES
BP --> FPS
BP --> SSS
BP --> TP
```

**Diagram sources**
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [TrackdubBuilder.cs](file://src/Trackdub.Sdk/TrackdubBuilder.cs)
- [TrackdubConfig.cs](file://src/Trackdub.Sdk/TrackdubConfig.cs)
- [TrackdubSession.cs](file://src/Trackdub.Sdk/TrackdubSession.cs)
- [TrackdubSessionFactory.cs](file://src/Trackdub.Sdk/TrackdubSessionFactory.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)
- [TrackdubProjectContext.cs](file://src/Trackdub.Sdk/TrackdubProjectContext.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IFileSystemProbe.cs](file://src/Trackdub.Contracts/IFileSystemProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)

**Section sources**
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [TrackdubBuilder.cs](file://src/Trackdub.Sdk/TrackdubBuilder.cs)
- [TrackdubConfig.cs](file://src/Trackdub.Sdk/TrackdubConfig.cs)
- [TrackdubSession.cs](file://src/Trackdub.Sdk/TrackdubSession.cs)
- [TrackdubSessionFactory.cs](file://src/Trackdub.Sdk/TrackdubSessionFactory.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)
- [TrackdubProjectContext.cs](file://src/Trackdub.Sdk/TrackdubProjectContext.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IFileSystemProbe.cs](file://src/Trackdub.Contracts/IFileSystemProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [IStudioSettingsService(IStudioSettingsService.cs)](file://src/Trackdub.Contracts/IStudioSettingsService.cs)

## Core Components
- CLI Entry Points
  - Program orchestrates command routing and launches wizards or tools.
  - DubSetupWizard guides users through project creation, metadata, and initial settings.
  - MediaIngestCommand performs one-shot ingestion with configurable options.

- SDK Orchestration
  - BatchFileDiscovery scans directories to find candidate media files.
  - BatchProcessor coordinates discovery, validation, extraction, and preprocessing.
  - TrackdubProjectPaths defines canonical folder layout for projects and artifacts.
  - TrackdubBuilder/Config configure pipeline stages, providers, and runtime options.
  - TrackdubSession/Factory manage lifecycle and context per run.
  - TrackdubProjectContextResolver/Context resolve and persist project state.

- Contracts
  - IMediaProbe and IFileSystemProbe provide media and filesystem introspection.
  - IAudioExtractionService handles audio stream extraction from containers.
  - IFileFingerprintService computes stable identifiers for deduplication and integrity.
  - IStudioSettingsService manages global and per-project settings.

**Section sources**
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [TrackdubBuilder.cs](file://src/Trackdub.Sdk/TrackdubBuilder.cs)
- [TrackdubConfig.cs](file://src/Trackdub.Sdk/TrackdubConfig.cs)
- [TrackdubSession.cs](file://src/Trackdub.Sdk/TrackdubSession.cs)
- [TrackdubSessionFactory.cs](file://src/Trackdub.Sdk/TrackdubSessionFactory.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)
- [TrackdubProjectContext.cs](file://src/Trackdub.Sdk/TrackdubProjectContext.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IFileSystemProbe.cs](file://src/Trackdub.Contracts/IFileSystemProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)

## Architecture Overview
The import and setup architecture follows a layered approach:
- User interaction via CLI commands and wizards
- Discovery and validation of media assets
- Extraction and preprocessing (audio/video)
- Quality assessment and guardrails
- Project initialization and persistence
- Pipeline configuration and execution readiness

```mermaid
sequenceDiagram
participant User as "User"
participant CLI as "Program.cs"
participant Wizard as "DubSetupWizard.cs"
participant Ingest as "MediaIngestCommand.cs"
participant Discovery as "BatchFileDiscovery.cs"
participant Processor as "BatchProcessor.cs"
participant Context as "TrackdubProjectContextResolver.cs"
participant Session as "TrackdubSession.cs"
participant Builder as "TrackdubBuilder.cs"
participant Probe as "IMediaProbe.cs"
participant FS as "IFileSystemProbe.cs"
participant Extract as "IAudioExtractionService.cs"
participant Fingerprint as "IFileFingerprintService.cs"
participant Settings as "IStudioSettingsService.cs"
User->>CLI : Invoke import/setup command
CLI->>Wizard : Launch wizard (optional)
CLI->>Ingest : Start ingestion (optional)
Ingest->>Discovery : Scan paths for media
Discovery-->>Ingest : Candidate files
Ingest->>Processor : Process candidates
Processor->>FS : Validate paths and permissions
Processor->>Probe : Detect media type and streams
Probe-->>Processor : Stream info
Processor->>Extract : Extract audio streams
Extract-->>Processor : Audio artifacts
Processor->>Fingerprint : Compute fingerprints
Fingerprint-->>Processor : IDs and checksums
Processor->>Settings : Read/write studio settings
Processor->>Context : Resolve/create project context
Context-->>Processor : Project paths and state
Processor->>Session : Create session and builder
Session->>Builder : Configure pipeline stages
Builder-->>Session : Ready pipeline
Session-->>Processor : Execution context
Processor-->>Ingest : Results and status
Ingest-->>CLI : Summary and next steps
CLI-->>User : Completion feedback
```

**Diagram sources**
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)
- [TrackdubSession.cs](file://src/Trackdub.Sdk/TrackdubSession.cs)
- [TrackdubBuilder.cs](file://src/Trackdub.Sdk/TrackdubBuilder.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IFileSystemProbe.cs](file://src/Trackdub.Contracts/IFileSystemProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)

## Detailed Component Analysis

### CLI Entry Points and Wizards
- Program coordinates command parsing and delegates to wizards or tools.
- DubSetupWizard walks users through:
  - Selecting source media
  - Creating or selecting a project directory
  - Configuring metadata (title, language, description)
  - Setting initial preferences (output format, loudness target, etc.)
- MediaIngestCommand supports non-interactive ingestion with flags for:
  - Input paths and filters
  - Output directory structure
  - Extraction and preprocessing options

```mermaid
flowchart TD
Start(["CLI Entry"]) --> ParseArgs["Parse Command Arguments"]
ParseArgs --> Mode{"Mode?"}
Mode --> |Wizard| LaunchWizard["Launch DubSetupWizard"]
Mode --> |Ingest| LaunchIngest["Launch MediaIngestCommand"]
LaunchWizard --> CollectMeta["Collect Metadata and Settings"]
LaunchIngest --> Discover["Discover Media Files"]
CollectMeta --> InitProject["Initialize Project Paths and State"]
InitProject --> Ready(["Ready for Processing"])
Discover --> Validate["Validate and Probe Media"]
Validate --> Extract["Extract Audio Streams"]
Extract --> Preprocess["Preprocess and Assess Quality"]
Preprocess --> SaveState["Persist Project and Artifacts"]
SaveState --> Ready
```

**Diagram sources**
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)

**Section sources**
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)

### SDK Orchestration: Discovery, Processing, and Context Resolution
- BatchFileDiscovery enumerates directories and applies filters to identify candidate media files.
- BatchOptions configures behavior such as recursion, inclusion/exclusion patterns, and output targets.
- BatchProcessor orchestrates:
  - Filesystem validation
  - Media probing and stream detection
  - Audio extraction and preprocessing
  - Fingerprinting for deduplication and integrity
  - Project context resolution and persistence
- TrackdubProjectPaths standardizes folder layout for inputs, outputs, artifacts, and metadata.
- TrackdubBuilder/Config set up pipeline stages, execution providers, and runtime options.
- TrackdubSession/Factory manage per-run lifecycle and resource management.
- TrackdubProjectContextResolver/Context resolve existing projects or create new ones based on inputs.

```mermaid
classDiagram
class BatchFileDiscovery {
+Scan(paths, filters) IEnumerable~string~
+FilterByExtensions()
+FilterBySize()
}
class BatchOptions {
+Recursive bool
+IncludePatterns string[]
+ExcludePatterns string[]
+OutputPath string
}
class BatchProcessor {
+Process(candidates, options) Result
-ValidateFiles(files)
-ProbeMedia(file) MediaInfo
-ExtractAudio(file) AudioArtifact
-ComputeFingerprint(file) Fingerprint
-ResolveProjectContext(options) ProjectContext
}
class TrackdubProjectPaths {
+Root string
+Inputs string
+Outputs string
+Artifacts string
+Metadata string
}
class TrackdubBuilder {
+ConfigureStages()
+SetExecutionProviders()
+Build() Session
}
class TrackdubConfig {
+PipelinePresets PipelinePreset
+RuntimeOptions RuntimeOptions
}
class TrackdubSession {
+Run(pipeline) Outcome
+Dispose()
}
class TrackdubSessionFactory {
+Create(config) Session
}
class TrackdubProjectContextResolver {
+Resolve(path) ProjectContext
+CreateNew(metadata) ProjectContext
}
class TrackdubProjectContext {
+Id string
+Paths ProjectPaths
+Metadata ProjectMetadata
+Save()
}
BatchProcessor --> BatchFileDiscovery : "uses"
BatchProcessor --> TrackdubProjectPaths : "uses"
BatchProcessor --> TrackdubProjectContextResolver : "uses"
BatchProcessor --> TrackdubSession : "creates"
TrackdubSession --> TrackdubSessionFactory : "created by"
TrackdubSession --> TrackdubBuilder : "configured by"
TrackdubBuilder --> TrackdubConfig : "reads"
```

**Diagram sources**
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [TrackdubBuilder.cs](file://src/Trackdub.Sdk/TrackdubBuilder.cs)
- [TrackdubConfig.cs](file://src/Trackdub.Sdk/TrackdubConfig.cs)
- [TrackdubSession.cs](file://src/Trackdub.Sdk/TrackdubSession.cs)
- [TrackdubSessionFactory.cs](file://src/Trackdub.Sdk/TrackdubSessionFactory.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)
- [TrackdubProjectContext.cs](file://src/Trackdub.Sdk/TrackdubProjectContext.cs)

**Section sources**
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [TrackdubBuilder.cs](file://src/Trackdub.Sdk/TrackdubBuilder.cs)
- [TrackdubConfig.cs](file://src/Trackdub.Sdk/TrackdubConfig.cs)
- [TrackdubSession.cs](file://src/Trackdub.Sdk/TrackdubSession.cs)
- [TrackdubSessionFactory.cs](file://src/Trackdub.Sdk/TrackdubSessionFactory.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)
- [TrackdubProjectContext.cs](file://src/Trackdub.Sdk/TrackdubProjectContext.cs)

### Contracts: Probing, Extraction, Fingerprinting, and Settings
- IMediaProbe exposes methods to detect container formats, codecs, and stream types.
- IFileSystemProbe validates paths, permissions, and disk availability.
- IAudioExtractionService extracts audio streams from video containers into normalized formats.
- IFileFingerprintService computes stable identifiers and checksums for deduplication and integrity verification.
- IStudioSettingsService reads/writes global and per-project settings affecting import behavior.

```mermaid
classDiagram
class IMediaProbe {
<<interface>>
+Detect(filePath) MediaInfo
+HasVideoStream(filePath) bool
+HasAudioStream(filePath) bool
}
class IFileSystemProbe {
<<interface>>
+Exists(path) bool
+CanWrite(path) bool
+GetDiskSpace(path) long
}
class IAudioExtractionService {
<<interface>>
+Extract(filePath, options) AudioArtifact
+SupportedContainers() string[]
}
class IFileFingerprintService {
<<interface>>
+Compute(filePath) Fingerprint
+Verify(filePath, fingerprint) bool
}
class IStudioSettingsService {
<<interface>>
+Get(key) object
+Set(key, value) void
+Export() SettingsSnapshot
+Import(snapshot) void
}
```

**Diagram sources**
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IFileSystemProbe.cs](file://src/Trackdub.Contracts/IFileSystemProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)

**Section sources**
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IFileSystemProbe.cs](file://src/Trackdub.Contracts/IFileSystemProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)

### Supported Formats, Automatic Detection, and Quality Assessment
- Supported formats are determined by IMediaProbe and IAudioExtractionService implementations. Typical containers include widely used video/audio formats; codecs vary by platform and runtime.
- Automatic detection uses probe services to identify:
  - Container type and version
  - Presence and count of audio/video streams
  - Codec details and bitrates
  - Duration and sample rates
- Quality assessment includes:
  - Loudness normalization targets
  - Peak and RMS checks
  - Silence detection and segment trimming
  - Guardrails to reject or flag low-quality inputs

```mermaid
flowchart TD
Start(["Input File"]) --> Probe["Probe Media Info"]
Probe --> Valid{"Valid Format?"}
Valid --> |No| Reject["Reject or Convert"]
Valid --> |Yes| Analyze["Analyze Streams"]
Analyze --> CheckQuality["Assess Quality Metrics"]
CheckQuality --> Pass{"Passes Guardrails?"}
Pass --> |No| Flag["Flag for Review"]
Pass --> |Yes| Proceed["Proceed to Extraction"]
Reject --> End(["End"])
Flag --> End
Proceed --> End
```

[No sources needed since this diagram shows conceptual workflow, not actual code structure]

**Section sources**
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [AudioPreparationTests.cs](file://tests/Trackdub.Application.Tests/AudioPreparationTests.cs)
- [SpeechAudioPreparationGuardrailTests.cs](file://tests/Trackdub.Application.Tests/SpeechAudioPreparationGuardrailTests.cs)

### Project Creation Wizard and Metadata Configuration
- The wizard collects:
  - Source media selection
  - Project name and root path
  - Language and target locale
  - Output preferences (format, loudness, bitrate)
  - Optional glossary or reference clips
- Metadata is persisted alongside project paths and settings for reproducibility.

```mermaid
sequenceDiagram
participant User as "User"
participant Wizard as "DubSetupWizard.cs"
participant Settings as "IStudioSettingsService.cs"
participant Paths as "TrackdubProjectPaths.cs"
participant Context as "TrackdubProjectContextResolver.cs"
User->>Wizard : Start wizard
Wizard->>User : Prompt for project name and path
Wizard->>User : Select source media
Wizard->>Settings : Load defaults
Wizard->>Paths : Initialize project folders
Wizard->>Context : Create or resolve project context
Context-->>Wizard : Persisted state
Wizard-->>User : Confirm setup complete
```

**Diagram sources**
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)

**Section sources**
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [TrackdubProjectContextResolver.cs](file://src/Trackdub.Sdk/TrackdubProjectContextResolver.cs)

### Audio Extraction Options and Video Format Handling
- Extraction options include:
  - Target sample rate and channel layout
  - Normalization and loudness targets
  - Segment trimming and silence removal
  - Codec selection for intermediate artifacts
- Video format handling involves:
  - Container probing and codec compatibility checks
  - Stream selection (primary audio track)
  - Fallback conversions when necessary

```mermaid
flowchart TD
Start(["Video File"]) --> Probe["Probe Video Streams"]
Probe --> Select["Select Primary Audio Stream"]
Select --> Normalize["Normalize Sample Rate and Channels"]
Normalize --> Loudness["Apply Loudness Target"]
Loudness --> Trim["Trim Silence and Segments"]
Trim --> Encode["Encode to Intermediate Format"]
Encode --> Done(["Audio Artifact Ready"])
```

**Diagram sources**
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)

**Section sources**
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)

### Preprocessing Steps and Workspace Organization
- Preprocessing includes:
  - Fingerprint computation for deduplication
  - Quality checks and guardrails
  - Artifact generation (waveforms, summaries)
- Workspace organization follows TrackdubProjectPaths:
  - Inputs: original media
  - Outputs: final artifacts
  - Artifacts: intermediates and metadata
  - Metadata: project settings and logs

```mermaid
flowchart TD
Start(["Media Ingest"]) --> Fingerprint["Compute Fingerprint"]
Fingerprint --> Dedup{"Duplicate?"}
Dedup --> |Yes| Skip["Skip or Merge"]
Dedup --> |No| Prep["Preprocess and Generate Artifacts"]
Prep --> Organize["Organize Workspace"]
Organize --> Persist["Persist Metadata and State"]
Persist --> Done(["Ready for Dubbing"])
```

**Diagram sources**
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)

**Section sources**
- [IFileFingerprintService.cs](file://src/Trackdub.Contracts/IFileFingerprintService.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)

## Dependency Analysis
Key dependencies and relationships:
- CLI depends on SDK for orchestration and contracts for abstractions.
- SDK depends on contracts for probing, extraction, fingerprinting, and settings.
- Application tests validate preprocessing and guardrails.

```mermaid
graph TB
CLI["CLI (Program.cs, DubSetupWizard.cs, MediaIngestCommand.cs)"] --> SDK["SDK (Batch*, Trackdub*)"]
SDK --> Contracts["Contracts (IMediaProbe, IAudioExtractionService, etc.)"]
SDK --> Tests["Tests (AudioPreparation*, SpeechAudioPreparation*)"]
```

**Diagram sources**
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [AudioPreparationTests.cs](file://tests/Trackdub.Application.Tests/AudioPreparationTests.cs)
- [SpeechAudioPreparationGuardrailTests.cs](file://tests/Trackdub.Application.Tests/SpeechAudioPreparationGuardrailTests.cs)

**Section sources**
- [Program.cs](file://src/Trackdub.Cli/Program.cs)
- [DubSetupWizard.cs](file://src/Trackdub.Cli/DubSetupWizard.cs)
- [MediaIngestCommand.cs](file://src/Trackdub.Tools/MediaIngestCommand.cs)
- [BatchFileDiscovery.cs](file://src/Trackdub.Sdk/BatchFileDiscovery.cs)
- [BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [TrackdubProjectPaths.cs](file://src/Trackdub.Sdk/TrackdubProjectPaths.cs)
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IAudioExtractionService.cs](file://src/Trackdub.Contracts/IAudioExtractionService.cs)
- [AudioPreparationTests.cs](file://tests/Trackdub.Application.Tests/AudioPreparationTests.cs)
- [SpeechAudioPreparationGuardrailTests.cs](file://tests/Trackdub.Application.Tests/SpeechAudioPreparationGuardrailTests.cs)

## Performance Considerations
- Large file handling:
  - Use streaming extraction where possible to avoid loading entire files into memory.
  - Prefer lossless intermediate formats for speed and fidelity.
  - Enable parallel processing for independent segments when safe.
- Disk and I/O:
  - Ensure sufficient free space in output directories.
  - Use fast storage for temporary artifacts to reduce latency.
- GPU/CPU utilization:
  - Configure execution providers appropriately via TrackdubBuilder/Config.
  - Monitor device readiness and fallback gracefully.
- Caching and deduplication:
  - Leverage fingerprinting to skip redundant work.
  - Cache probe results for repeated operations.

[No sources needed since this section provides general guidance]

## Troubleshooting Guide
Common import issues and recovery strategies:
- Unsupported or corrupted files:
  - Verify container and codec support via IMediaProbe.
  - Attempt conversion or re-encode using external tools if necessary.
- Permission or path errors:
  - Use IFileSystemProbe to check existence and write permissions.
  - Adjust paths or run with appropriate privileges.
- Low-quality audio:
  - Review guardrail thresholds and adjust normalization targets.
  - Inspect waveforms and segment trimming results.
- Memory or performance issues:
  - Reduce concurrency or segment size.
  - Optimize execution provider settings.

**Section sources**
- [IMediaProbe.cs](file://src/Trackdub.Contracts/IMediaProbe.cs)
- [IFileSystemProbe.cs](file://src/Trackdub.Contracts/IFileSystemProbe.cs)
- [AudioPreparationTests.cs](file://tests/Trackdub.Application.Tests/AudioPreparationTests.cs)
- [SpeechAudioPreparationGuardrailTests.cs](file://tests/Trackdub.Application.Tests/SpeechAudioPreparationGuardrailTests.cs)
- [ProjectMediaIngestServiceTests.cs](file://tests/Trackdub.Application.Tests/ProjectMediaIngestServiceTests.cs)

## Conclusion
Trackdub’s media import and setup process combines robust CLI tools, flexible SDK orchestration, and well-defined contracts to deliver a reliable workflow for interactive dubbing. By leveraging automatic detection, quality assessment, and standardized project structures, users can efficiently prepare media assets for dubbing while maintaining control over metadata, settings, and performance characteristics.

[No sources needed since this section summarizes without analyzing specific files]