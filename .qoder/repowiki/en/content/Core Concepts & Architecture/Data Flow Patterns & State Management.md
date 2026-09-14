# Data Flow Patterns & State Management

<cite>
**Referenced Files in This Document**
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Domain.csproj](file://src/Trackdub.Domain/Trackdub.Domain.csproj)
- [Trackdub.Infrastructure.csproj](file://src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj)
- [Trackdub.Contracts.csproj](file://src/Trackdub.Contracts/Trackdub.Contracts.csproj)
- [Trackdub.Media.csproj](file://src/Trackdub.Media/Trackdub.Media.csproj)
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)
- [Trackdub.Inference.Onnx.csproj](file://src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj)
- [Trackdub.Sdk.csproj](file://src/Trackdub.Sdk/Trackdub.Sdk.csproj)
- [Trackdub.Composition.csproj](file://src/Trackdub.Composition/Trackdub.Composition.csproj)
- [ARCHITECTURE.md](file://docs/architecture/ARCHITECTURE.md)
- [ADR-0002-sqlite-project-persistence.md](file://docs/decisions/ADR-0002-sqlite-project-persistence.md)
- [ADR-0010-event-sourced-pipeline.md](file://docs/decisions/ADR-0010-event-sourced-pipeline.md)
- [pipeline-principles-review-grok-2026-07-08.md](file://docs/architecture/pipeline-principles-review-grok-2026-07-08.md)
- [local-pipeline-audit-unified-2026-07-08.md](file://docs/architecture/local-pipeline-audit-unified-2026-07-08.md)
- [ProjectSessionServiceTests.cs](file://tests/Trackdub.Application.Tests/ProjectSessionServiceTests.cs)
- [AsrStageHandlerTests.cs](file://tests/Trackdub.Application.Tests/AsrStageHandlerTests.cs)
- [SpeakerAssignmentAndPersistenceStageTests.cs](file://tests/Trackdub.Application.Tests/SpeakerAssignmentAndPersistenceStageTests.cs)
- [OrchestrationServiceTests.cs](file://tests/Trackdub.Application.Tests/OrchestrationServiceTests.cs)
</cite>

## Table of Contents
1. Introduction
2. Project Structure
3. Core Components
4. Architecture Overview
5. Detailed Component Analysis
6. Dependency Analysis
7. Performance Considerations
8. Troubleshooting Guide
9. Conclusion

## Introduction
This document explains Trackdub’s data flow patterns and state management strategies across the pipeline from media ingestion to final output. It covers domain models for projects, sessions, transcripts, and speakers; persistence using SQLite; caching strategies; synchronization patterns; validation and transformation pipelines; event-driven updates; concurrency and transaction management; and consistency guarantees across components. The goal is to provide a clear mental model for both new contributors and experienced developers working on data-centric features.

## Project Structure
Trackdub is organized into layered packages that separate concerns:
- Contracts define interfaces and shared types used across layers.
- Domain holds core business entities and rules.
- Application orchestrates workflows, stages, and services.
- Infrastructure implements persistence (SQLite), file system access, and other cross-cutting concerns.
- Media provides audio/video processing utilities.
- Inference encapsulates AI runtime orchestration and execution providers.
- Composition wires dependencies and bootstraps contexts.
- Sdk exposes a programmatic API for external automation.

```mermaid
graph TB
subgraph "Contracts"
C1["ApplicationContracts"]
C2["Pipeline"]
C3["Transcripts"]
C4["Projects"]
C5["Persistence"]
end
subgraph "Domain"
D1["Projects"]
D2["Transcript"]
D3["Speakers"]
D4["Artifacts"]
D5["StageRuns"]
end
subgraph "Application"
A1["Pipeline"]
A2["Transcripts"]
A3["Projects"]
A4["Services"]
end
subgraph "Infrastructure"
I1["Persistence"]
I2["FileSystem"]
I3["Diagnostics"]
end
subgraph "Media"
M1["Extraction"]
M2["Mixing"]
M3["Waveforms"]
end
subgraph "Inference"
R1["Runtime"]
R2["Pipelines"]
R3["Onnx"]
end
subgraph "Composition"
CO1["CompositionRoot"]
CO2["WorkspaceContext"]
end
subgraph "Sdk"
S1["TrackdubBuilder"]
S2["TrackdubSession"]
end
C1 --> A1
C2 --> A1
C3 --> A2
C4 --> A3
C5 --> I1
D1 --> A3
D2 --> A2
D3 --> A2
D4 --> A1
D5 --> A1
A1 --> I1
A1 --> R2
A2 --> I1
A3 --> I1
I1 --> D1
I1 --> D2
I1 --> D3
R2 --> R3
R2 --> R1
CO1 --> A1
CO1 --> A2
CO1 --> A3
S1 --> CO1
S2 --> CO1
```

**Diagram sources**
- [Trackdub.Contracts.csproj](file://src/Trackdub.Contracts/Trackdub.Contracts.csproj)
- [Trackdub.Domain.csproj](file://src/Trackdub.Domain/Trackdub.Domain.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Infrastructure.csproj](file://src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj)
- [Trackdub.Media.csproj](file://src/Trackdub.Media/Trackdub.Media.csproj)
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)
- [Trackdub.Inference.Onnx.csproj](file://src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj)
- [Trackdub.Composition.csproj](file://src/Trackdub.Composition/Trackdub.Composition.csproj)
- [Trackdub.Sdk.csproj](file://src/Trackdub.Sdk/Trackdub.Sdk.csproj)

**Section sources**
- [ARCHITECTURE.md](file://docs/architecture/ARCHITECTURE.md)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Domain.csproj](file://src/Trackdub.Domain/Trackdub.Domain.csproj)
- [Trackdub.Infrastructure.csproj](file://src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj)
- [Trackdub.Contracts.csproj](file://src/Trackdub.Contracts/Trackdub.Contracts.csproj)
- [Trackdub.Media.csproj](file://src/Trackdub.Media/Trackdub.Media.csproj)
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)
- [Trackdub.Inference.Onnx.csproj](file://src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj)
- [Trackdub.Composition.csproj](file://src/Trackdub.Composition/Trackdub.Composition.csproj)
- [Trackdub.Sdk.csproj](file://src/Trackdub.Sdk/Trackdub.Sdk.csproj)

## Core Components
- Projects: Represent user workspaces containing media assets, sessions, and artifacts.
- Sessions: Scoped units of work within a project, tracking progress and outputs.
- Transcripts: Structured text with timing and speaker assignments derived from ASR and diarization.
- Speakers: Entities representing distinct voice identities linked to transcript segments.
- Stage Runs: Records of individual pipeline stage executions with provenance and outcomes.
- Artifacts: Output files and metadata produced by stages (audio, subtitles, alignments).

These models are persisted via SQLite and updated through event-driven mechanisms during pipeline execution. Validation occurs at ingestion and between stages to ensure integrity.

**Section sources**
- [Trackdub.Domain.csproj](file://src/Trackdub.Domain/Trackdub.Domain.csproj)
- [Trackdub.Contracts.csproj](file://src/Trackdub.Contracts/Trackdub.Contracts.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)

## Architecture Overview
The pipeline follows an event-sourced pattern where each stage emits events that drive downstream transformations and UI updates. Data flows from media ingestion through preprocessing, ASR, diarization, translation/refinement, TTS generation, lip-sync, mixing, and export. Each step validates inputs, transforms data, and persists intermediate results.

```mermaid
sequenceDiagram
participant Client as "Client/SDK"
participant Builder as "TrackdubBuilder"
participant Session as "TrackdubSession"
participant Orchestrator as "OrchestrationService"
participant Pipeline as "Pipeline Stages"
participant Persistence as "SQLite Repository"
participant Cache as "Model Cache"
participant Media as "Media Services"
participant Inference as "Inference Pipelines"
Client->>Builder : Configure options
Builder->>Session : Create session
Session->>Orchestrator : Start run
Orchestrator->>Pipeline : Execute stages sequentially
Pipeline->>Media : Extract audio, probe metadata
Media-->>Pipeline : Audio streams, metadata
Pipeline->>Inference : Run ASR/diarization/translation/TTS
Inference-->>Pipeline : Transcript segments, timings, voices
Pipeline->>Persistence : Persist stage runs, transcripts, speakers
Persistence-->>Pipeline : Verified records
Pipeline-->>Orchestrator : Emit progress events
Orchestrator-->>Session : Update UI state
Session-->>Client : Final artifacts and status
```

**Diagram sources**
- [Trackdub.Sdk.csproj](file://src/Trackdub.Sdk/Trackdub.Sdk.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Infrastructure.csproj](file://src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj)
- [Trackdub.Media.csproj](file://src/Trackdub.Media/Trackdub.Media.csproj)
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)

## Detailed Component Analysis

### Media Ingestion and Validation
- Media ingestion extracts audio streams, probes format/sample rate, and normalizes to a canonical representation.
- Validation checks supported formats, duration limits, and loudness thresholds before proceeding.
- Intermediate artifacts include normalized PCM buffers and waveform summaries.

```mermaid
flowchart TD
Start(["Start Ingestion"]) --> Probe["Probe Media Metadata"]
Probe --> ValidateFormat{"Format Supported?"}
ValidateFormat --> |No| Reject["Reject Input"]
ValidateFormat --> |Yes| Extract["Extract Audio Stream"]
Extract --> Normalize["Normalize Sample Rate/Channels"]
Normalize --> ValidateLoudness{"Loudness Within Limits?"}
ValidateLoudness --> |No| Adjust["Apply Loudness Adjustment"]
ValidateLoudness --> |Yes| Store["Store Normalized Buffer"]
Adjust --> Store
Store --> End(["Ready for ASR"])
```

**Diagram sources**
- [Trackdub.Media.csproj](file://src/Trackdub.Media/Trackdub.Media.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)

**Section sources**
- [Trackdub.Media.csproj](file://src/Trackdub.Media/Trackdub.Media.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)

### ASR and Diarization Pipeline
- ASR converts audio to time-aligned word/phrase segments.
- Diarization clusters speaker turns and assigns speaker IDs.
- Outputs are validated for coverage and overlap constraints.

```mermaid
sequenceDiagram
participant Stage as "ASR/Diarization Stage"
participant Inference as "Inference Pipelines"
participant Validator as "Validation Service"
participant Repo as "SQLite Repository"
Stage->>Inference : Run ASR on normalized audio
Inference-->>Stage : Raw transcript segments
Stage->>Validator : Validate segment timings and coverage
Validator-->>Stage : Pass/Fail with corrections
Stage->>Inference : Run diarization clustering
Inference-->>Stage : Speaker assignments
Stage->>Repo : Persist transcript + speaker mappings
Repo-->>Stage : Confirm persistence
```

**Diagram sources**
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Infrastructure.csproj](file://src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj)

**Section sources**
- [AsrStageHandlerTests.cs](file://tests/Trackdub.Application.Tests/AsrStageHandlerTests.cs)
- [SpeakerAssignmentAndPersistenceStageTests.cs](file://tests/Trackdub.Application.Tests/SpeakerAssignmentAndPersistenceStageTests.cs)

### Translation and Text Refinement
- Translates source transcript to target language while preserving timing.
- Applies glossary enforcement and stylistic refinement rules.
- Validates semantic fidelity and length constraints relative to original segments.

```mermaid
flowchart TD
Start(["Input Transcript"]) --> Translate["Translate Segments"]
Translate --> GlossaryCheck{"Glossary Terms Present?"}
GlossaryCheck --> |No| Refine["Apply Stylistic Refinement"]
GlossaryCheck --> |Yes| Enforce["Enforce Glossary Constraints"]
Enforce --> Refine
Refine --> ValidateLength{"Length Within Limits?"}
ValidateLength --> |No| Adjust["Adjust Segment Boundaries"]
ValidateLength --> |Yes| Persist["Persist Refined Transcript"]
Adjust --> Persist
Persist --> End(["Output Ready"])
```

**Diagram sources**
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Contracts.csproj](file://src/Trackdub.Contracts/Trackdub.Contracts.csproj)

**Section sources**
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Contracts.csproj](file://src/Trackdub.Contracts/Trackdub.Contracts.csproj)

### TTS Generation and Lip-Sync
- Generates speech audio per speaker using selected TTS engine.
- Aligns generated audio with transcript timings and applies prosody adjustments.
- Lip-sync synthesizes visual cues based on phoneme timing.

```mermaid
sequenceDiagram
participant TtsStage as "TTS Stage"
participant Engine as "TTS Engine"
participant Aligner as "Alignment Service"
participant LipSync as "Lip-Sync Module"
participant Repo as "SQLite Repository"
TtsStage->>Engine : Generate audio per segment
Engine-->>TtsStage : Synthesized audio chunks
TtsStage->>Aligner : Align audio to transcript timings
Aligner-->>TtsStage : Aligned audio tracks
TtsStage->>LipSync : Compute lip movements from phonemes
LipSync-->>TtsStage : Lip-sync data
TtsStage->>Repo : Persist audio artifacts and sync data
Repo-->>TtsStage : Confirmation
```

**Diagram sources**
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)
- [Trackdub.Infrastructure.csproj](file://src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj)

**Section sources**
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)

### Mixing and Export
- Mixes multiple audio tracks (original, TTS, effects) into final output.
- Applies normalization, compression, and channel mapping.
- Exports video/audio artifacts with embedded subtitles and metadata.

```mermaid
flowchart TD
Start(["Audio Tracks"]) --> Mix["Mix Tracks"]
Mix --> Normalize["Normalize Levels"]
Normalize --> Compress["Apply Compression"]
Compress --> MapChannels["Map Channels"]
MapChannels --> Export["Export Final Artifact"]
Export --> End(["Complete"])
```

**Diagram sources**
- [Trackdub.Media.csproj](file://src/Trackdub.Media/Trackdub.Media.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)

**Section sources**
- [Trackdub.Media.csproj](file://src/Trackdub.Media/Trackdub.Media.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)

## Dependency Analysis
Trackdub enforces strict layering:
- Contracts define interfaces consumed by Application and Infrastructure.
- Domain models are immutable and referenced by Application services.
- Infrastructure implements persistence and file system operations.
- Inference encapsulates AI runtime dependencies.
- Composition wires all components together.

```mermaid
graph LR
Contracts["Contracts"] --> Application["Application"]
Contracts --> Infrastructure["Infrastructure"]
Domain["Domain"] --> Application
Application --> Infrastructure
Application --> Inference["Inference"]
Composition["Composition"] --> Application
Composition --> Infrastructure
Composition --> Inference
Sdk["Sdk"] --> Composition
```

**Diagram sources**
- [Trackdub.Contracts.csproj](file://src/Trackdub.Contracts/Trackdub.Contracts.csproj)
- [Trackdub.Domain.csproj](file://src/Trackdub.Domain/Trackdub.Domain.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Infrastructure.csproj](file://src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj)
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)
- [Trackdub.Composition.csproj](file://src/Trackdub.Composition/Trackdub.Composition.csproj)
- [Trackdub.Sdk.csproj](file://src/Trackdub.Sdk/Trackdub.Sdk.csproj)

**Section sources**
- [Trackdub.Contracts.csproj](file://src/Trackdub.Contracts/Trackdub.Contracts.csproj)
- [Trackdub.Domain.csproj](file://src/Trackdub.Domain/Trackdub.Domain.csproj)
- [Trackdub.Application.csproj](file://src/Trackdub.Application/Trackdub.Application.csproj)
- [Trackdub.Infrastructure.csproj](file://src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj)
- [Trackdub.Inference.csproj](file://src/Trackdub.Inference/Trackdub.Inference.csproj)
- [Trackdub.Composition.csproj](file://src/Trackdub.Composition/Trackdub.Composition.csproj)
- [Trackdub.Sdk.csproj](file://src/Trackdub.Sdk/Trackdub.Sdk.csproj)

## Performance Considerations
- Model caching reduces startup latency and repeated downloads.
- Batched inference minimizes overhead for large transcripts.
- Streaming audio processing avoids loading entire files into memory.
- SQLite transactions group writes to reduce disk I/O.
- GPU acceleration via execution providers improves ASR/TTS throughput.

[No sources needed since this section provides general guidance]

## Troubleshooting Guide
Common issues and resolutions:
- Invalid media format: Ensure input conforms to supported codecs and sample rates.
- ASR failures: Check audio quality, noise levels, and model availability.
- Diarization errors: Verify sufficient speaker separation and audio clarity.
- Persistence conflicts: Use proper transaction boundaries and retry logic.
- Event synchronization: Monitor pipeline events for stalled stages.

**Section sources**
- [ProjectSessionServiceTests.cs](file://tests/Trackdub.Application.Tests/ProjectSessionServiceTests.cs)
- [OrchestrationServiceTests.cs](file://tests/Trackdub.Application.Tests/OrchestrationServiceTests.cs)

## Conclusion
Trackdub’s data flow combines robust validation, event-driven orchestration, and persistent state management to deliver reliable transcription, translation, and dubbing workflows. By adhering to layered architecture principles and leveraging SQLite for consistent storage, the system ensures scalability and maintainability while supporting complex media processing tasks.

[No sources needed since this section summarizes without analyzing specific files]