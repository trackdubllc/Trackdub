# Voice Selection & TTS Configuration

<cite>
**Referenced Files in This Document**
- [Trackdub.Domain/Tts/](file://src/Trackdub.Domain/Tts)
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Inference.Onnx/Kokoro/](file://src/Trackdub.Inference.Onnx/Kokoro)
- [Trackdub.Inference.Onnx/Qwen3Tts/](file://src/Trackdub.Inference.Onnx/Qwen3Tts)
- [Trackdub.Infrastructure/Tts/](file://src/Trackdub.Infrastructure/Tts)
- [Trackdub.Media/Tts/](file://src/Trackdub.Media/Tts)
- [Trackdub.Cli/Interactive/](file://src/Trackdub.Cli/Interactive)
- [Trackdub.Benchmarks/DubbingBenchmarkRunner.cs](file://src/Trackdub.Benchmarks/DubbingBenchmarkRunner.cs)
- [Trackdub.Sdk/BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [Trackdub.Sdk/BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [Trackdub.Sdk/PresetStore.cs](file://src/Trackdub.Sdk/PresetStore.cs)
- [Trackdub.Sdk/PipelinePreset.cs](file://src/Trackdub.Sdk/PipelinePreset.cs)
- [Trackdub.Composition/StarterPacks/](file://src/Trackdub.Composition/StarterPacks)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/IModelDownloadOrchestrator.cs](file://src/Trackdub.Contracts/IModelDownloadOrchestrator.cs)
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)
- [Trackdub.Contracts/ISpeechAudioEnhancementService.cs](file://src/Trackdub.Contracts/ISpeechAudioEnhancementService.cs)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)
- [Trackdub.Contracts/ISpeakerConsentService.cs](file://src/Trackdub.Contracts/ISpeakerConsentService.cs)
- [Trackdub.Contracts/IDiagnosticsBundleExporter.cs](file://src/Trackdub.Contracts/IDiagnosticsBundleExporter.cs)
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
10. [Appendices](#appendices)

## Introduction
This document explains how voice selection and text-to-speech (TTS) configuration work within the interactive workflow. It covers the voice catalog interface, preview capabilities, quality comparison tools, engine selection, parameter tuning, voice cloning options, emotional tone adjustment, speech rate control, pronunciation customization, real-time synthesis testing, batch application, template usage, and custom voice pack integration. The goal is to help both new users and advanced practitioners understand the end-to-end flow from selecting a voice to generating high-quality synthesized audio with full auditability and reproducibility.

## Project Structure
The TTS-related functionality spans multiple layers:
- Domain models for TTS entities and relationships
- Contracts that define interfaces for repositories, services, and diagnostics
- Application handlers orchestrating candidate generation and pipeline stages
- Inference implementations for specific TTS engines
- Infrastructure components for settings, persistence, and model management
- Media utilities for post-processing and waveform analysis
- CLI interactive modules for user workflows
- SDK and benchmarking for batch operations and performance evaluation

```mermaid
graph TB
subgraph "Domain"
D_TTS["TTS Models"]
end
subgraph "Contracts"
C_CandidateRepo["ITtsCandidateGroupRepository"]
C_Audit["IVoiceCloneAuditLog"]
C_Settings["IStudioSettingsService"]
C_Inventory["IModelInventoryService"]
C_Download["IModelDownloadOrchestrator"]
C_Preview["IAudioPreviewTransport"]
C_Waveform["IWaveformSummaryGenerator"]
C_PostProc["ITtsAudioPostProcessor"]
C_Engine["ISpeechAudioEnhancementService"]
C_Consent["ISpeakerConsentService"]
C_Diag["IDiagnosticsBundleExporter"]
end
subgraph "Application"
A_Handler["GenerateCandidatesHandler"]
end
subgraph "Inference"
I_Kokoro["Kokoro Engine"]
I_Qwen3Tts["Qwen3Tts Engine"]
end
subgraph "Infrastructure"
INF_Settings["Settings & Persistence"]
INF_Models["Model Inventory & Downloads"]
end
subgraph "Media"
M_PostProc["TTS Post-Processing"]
M_Waveform["Waveform Summary"]
end
subgraph "CLI Interactive"
CLI_Inter["Interactive Workflow"]
end
subgraph "SDK & Benchmarks"
SDK_Batch["BatchProcessor"]
SDK_Options["BatchOptions"]
SDK_Presets["PipelinePreset / PresetStore"]
BM_Runner["DubbingBenchmarkRunner"]
end
D_TTS --> C_CandidateRepo
A_Handler --> C_CandidateRepo
A_Handler --> C_Settings
A_Handler --> C_Inventory
A_Handler --> C_Download
A_Handler --> I_Kokoro
A_Handler --> I_Qwen3Tts
I_Kokoro --> M_PostProc
I_Qwen3Tts --> M_PostProc
M_PostProc --> C_PostProc
M_PostProc --> C_Waveform
CLI_Inter --> A_Handler
CLI_Inter --> C_Preview
SDK_Batch --> SDK_Options
SDK_Batch --> SDK_Presets
BM_Runner --> SDK_Batch
```

**Diagram sources**
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/IModelDownloadOrchestrator.cs](file://src/Trackdub.Contracts/IModelDownloadOrchestrator.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)
- [Trackdub.Contracts/ISpeechAudioEnhancementService.cs](file://src/Trackdub.Contracts/ISpeechAudioEnhancementService.cs)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)
- [Trackdub.Inference.Onnx/Kokoro/](file://src/Trackdub.Inference.Onnx/Kokoro)
- [Trackdub.Inference.Onnx/Qwen3Tts/](file://src/Trackdub.Inference.Onnx/Qwen3Tts)
- [Trackdub.Infrastructure/Tts/](file://src/Trackdub.Infrastructure/Tts)
- [Trackdub.Media/Tts/](file://src/Trackdub.Media/Tts)
- [Trackdub.Cli/Interactive/](file://src/Trackdub.Cli/Interactive)
- [Trackdub.Sdk/BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [Trackdub.Sdk/BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [Trackdub.Sdk/PresetStore.cs](file://src/Trackdub.Sdk/PresetStore.cs)
- [Trackdub.Sdk/PipelinePreset.cs](file://src/Trackdub.Sdk/PipelinePreset.cs)
- [Trackdub.Benchmarks/DubbingBenchmarkRunner.cs](file://src/Trackdub.Benchmarks/DubbingBenchmarkRunner.cs)

**Section sources**
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/IModelDownloadOrchestrator.cs](file://src/Trackdub.Contracts/IModelDownloadOrchestrator.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)
- [Trackdub.Contracts/ISpeechAudioEnhancementService.cs](file://src/Trackdub.Contracts/ISpeechAudioEnhancementService.cs)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)
- [Trackdub.Inference.Onnx/Kokoro/](file://src/Trackdub.Inference.Onnx/Kokoro)
- [Trackdub.Inference.Onnx/Qwen3Tts/](file://src/Trackdub.Inference.Onnx/Qwen3Tts)
- [Trackdub.Infrastructure/Tts/](file://src/Trackdub.Infrastructure/Tts)
- [Trackdub.Media/Tts/](file://src/Trackdub.Media/Tts)
- [Trackdub.Cli/Interactive/](file://src/Trackdub.Cli/Interactive)
- [Trackdub.Sdk/BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [Trackdub.Sdk/BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [Trackdub.Sdk/PresetStore.cs](file://src/Trackdub.Sdk/PresetStore.cs)
- [Trackdub.Sdk/PipelinePreset.cs](file://src/Trackdub.Sdk/PipelinePreset.cs)
- [Trackdub.Benchmarks/DubbingBenchmarkRunner.cs](file://src/Trackdub.Benchmarks/DubbingBenchmarkRunner.cs)

## Core Components
- TTS domain models encapsulate voice metadata, candidate groups, and synthesis parameters such as speed, emotion, and pronunciation rules.
- Candidate group repository provides access to grouped voice candidates for selection and comparison.
- Voice clone audit log records cloning actions for compliance and traceability.
- Studio settings service centralizes user preferences for TTS behavior and UI defaults.
- Model inventory and download orchestrator manage availability and lifecycle of TTS engines and voice packs.
- Audio preview transport streams synthesized previews to playback backends.
- Waveform summary generator computes metrics for quick visual assessment.
- Speech audio enhancement and TTS post-processing ensure consistent output quality.
- Speaker consent service enforces rights and permissions for cloned voices.
- Diagnostics bundle exporter aggregates logs and artifacts for troubleshooting.

**Section sources**
- [Trackdub.Domain/Tts/](file://src/Trackdub.Domain/Tts)
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/IModelDownloadOrchestrator.cs](file://src/Trackdub.Contracts/IModelDownloadOrchestrator.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)
- [Trackdub.Contracts/ISpeechAudioEnhancementService.cs](file://src/Trackdub.Contracts/ISpeechAudioEnhancementService.cs)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)
- [Trackdub.Contracts/ISpeakerConsentService.cs](file://src/Trackdub.Contracts/ISpeakerConsentService.cs)
- [Trackdub.Contracts/IDiagnosticsBundleExporter.cs](file://src/Trackdub.Contracts/IDiagnosticsBundleExporter.cs)

## Architecture Overview
The interactive workflow begins with the CLI or UI prompting the user to select a voice and configure TTS parameters. The application handler coordinates candidate generation by consulting the candidate repository and model inventory, then invokes the selected TTS engine implementation. Synthesized audio passes through post-processing and enhancement services before being streamed to the preview transport. Quality metrics are computed via waveform summaries and stored alongside candidate groups for comparison.

```mermaid
sequenceDiagram
participant User as "User"
participant CLI as "CLI Interactive"
participant Handler as "GenerateCandidatesHandler"
participant Repo as "ITtsCandidateGroupRepository"
participant Inv as "IModelInventoryService"
participant Eng as "TTS Engine (Kokoro/Qwen3Tts)"
participant Post as "ITtsAudioPostProcessor"
participant Enh as "ISpeechAudioEnhancementService"
participant Preview as "IAudioPreviewTransport"
participant Metrics as "IWaveformSummaryGenerator"
User->>CLI : Select voice & set parameters
CLI->>Handler : Request candidate generation
Handler->>Repo : Load candidate groups
Handler->>Inv : Verify engine availability
Handler->>Eng : Synthesize audio with parameters
Eng-->>Handler : Raw audio stream
Handler->>Post : Apply post-processing
Handler->>Enh : Enhance speech audio
Handler-->>Preview : Stream preview
Handler->>Metrics : Compute waveform summary
Handler-->>CLI : Present results & metrics
```

**Diagram sources**
- [Trackdub.Cli/Interactive/](file://src/Trackdub.Cli/Interactive)
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Inference.Onnx/Kokoro/](file://src/Trackdub.Inference.Onnx/Kokoro)
- [Trackdub.Inference.Onnx/Qwen3Tts/](file://src/Trackdub.Inference.Onnx/Qwen3Tts)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)
- [Trackdub.Contracts/ISpeechAudioEnhancementService.cs](file://src/Trackdub.Contracts/ISpeechAudioEnhancementService.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)

## Detailed Component Analysis

### Voice Catalog Interface
- Presents available voices grouped by engine, language, and style.
- Supports filtering by attributes like gender, age range, and accent.
- Displays metadata such as license status, source model, and version.
- Integrates with model inventory to reflect current availability and readiness.

```mermaid
flowchart TD
Start(["Open Voice Catalog"]) --> FetchGroups["Load Candidate Groups"]
FetchGroups --> FilterUI{"Apply Filters?"}
FilterUI --> |Yes| ApplyFilters["Apply Attribute Filters"]
FilterUI --> |No| ShowList["Show Voice List"]
ApplyFilters --> ShowList
ShowList --> SelectVoice["Select Voice"]
SelectVoice --> ValidateLicense["Check License & Consent"]
ValidateLicense --> Ready{"Ready to Use?"}
Ready --> |Yes| Proceed["Proceed to Preview"]
Ready --> |No| PromptAction["Prompt Action (e.g., Download/Consent)"]
PromptAction --> Proceed
```

**Section sources**
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/ISpeakerConsentService.cs](file://src/Trackdub.Contracts/ISpeakerConsentService.cs)

### Preview Capabilities
- Generates short audio snippets based on selected voice and parameters.
- Streams previews in real time using the audio preview transport.
- Provides waveform visualization and basic metrics for immediate feedback.
- Supports AB comparison mode to toggle between two candidates.

```mermaid
sequenceDiagram
participant UI as "UI/CLI"
participant Handler as "GenerateCandidatesHandler"
participant Eng as "TTS Engine"
participant Preview as "IAudioPreviewTransport"
participant Metrics as "IWaveformSummaryGenerator"
UI->>Handler : Generate preview
Handler->>Eng : Synthesize snippet
Eng-->>Handler : Audio chunk stream
Handler->>Preview : Stream chunk
Handler->>Metrics : Compute metrics per chunk
Handler-->>UI : Update waveform & metrics
```

**Diagram sources**
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)

**Section sources**
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)

### Quality Comparison Tools
- Computes objective metrics such as loudness, clarity, and stability.
- Visualizes waveforms side-by-side for subjective comparison.
- Stores comparison results alongside candidate groups for later review.
- Enables export of comparison reports for team collaboration.

```mermaid
classDiagram
class CandidateGroup {
+string id
+string voiceId
+SynthesisResult[] results
+addResult(result) void
+getComparison() ComparisonReport
}
class SynthesisResult {
+string artifactPath
+float duration
+float loudness
+float clarityScore
+float stabilityScore
+metadata map
}
class ComparisonReport {
+SynthesisResult[] items
+computeDelta() DeltaMetrics
+export(path) void
}
CandidateGroup --> SynthesisResult : "contains"
CandidateGroup --> ComparisonReport : "generates"
```

**Diagram sources**
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)

**Section sources**
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)

### TTS Engine Selection
- Chooses among supported engines (e.g., Kokoro, Qwen3Tts) based on availability, performance, and user preference.
- Validates runtime readiness and execution provider compatibility.
- Applies engine-specific defaults when parameters are unspecified.

```mermaid
flowchart TD
Start(["Engine Selection"]) --> CheckPrefs["Read Studio Settings"]
CheckPrefs --> CheckInventory["Query Model Inventory"]
CheckInventory --> Available{"Available Engines?"}
Available --> |Yes| RankEngines["Rank by Performance & Preference"]
Available --> |No| PromptInstall["Prompt Installation/Download"]
PromptInstall --> CheckInventory
RankEngines --> SelectBest["Select Best Fit"]
SelectBest --> ValidateRuntime["Validate Runtime Readiness"]
ValidateRuntime --> Ready{"Ready?"}
Ready --> |Yes| UseEngine["Use Selected Engine"]
Ready --> |No| Fallback["Fallback to Alternative"]
Fallback --> UseEngine
```

**Diagram sources**
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Inference.Onnx/Kokoro/](file://src/Trackdub.Inference.Onnx/Kokoro)
- [Trackdub.Inference.Onnx/Qwen3Tts/](file://src/Trackdub.Inference.Onnx/Qwen3Tts)

**Section sources**
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Inference.Onnx/Kokoro/](file://src/Trackdub.Inference.Onnx/Kokoro)
- [Trackdub.Inference.Onnx/Qwen3Tts/](file://src/Trackdub.Inference.Onnx/Qwen3Tts)

### Parameter Tuning
- Speed control adjusts speech rate while preserving natural prosody.
- Emotional tone modulation influences intonation and expressiveness.
- Pronunciation customization allows phoneme-level overrides for specific terms.
- Parameters are validated against engine constraints and persisted for reuse.

```mermaid
flowchart TD
Start(["Parameter Input"]) --> Validate["Validate Against Constraints"]
Validate --> Valid{"Valid?"}
Valid --> |No| Error["Return Validation Errors"]
Valid --> |Yes| ApplyDefaults["Apply Engine Defaults"]
ApplyDefaults --> MergeParams["Merge User Overrides"]
MergeParams --> Persist["Persist for Reuse"]
Persist --> Ready["Ready for Synthesis"]
```

**Section sources**
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Inference.Onnx/Kokoro/](file://src/Trackdub.Inference.Onnx/Kokoro)
- [Trackdub.Inference.Onnx/Qwen3Tts/](file://src/Trackdub.Inference.Onnx/Qwen3Tts)

### Voice Cloning Options
- Captures reference audio samples and builds a voice profile.
- Enforces speaker consent checks before cloning proceeds.
- Records cloning actions in the audit log for compliance.
- Supports exporting cloned voice packs for distribution.

```mermaid
sequenceDiagram
participant User as "User"
participant Handler as "GenerateCandidatesHandler"
participant Consent as "ISpeakerConsentService"
participant Audit as "IVoiceCloneAuditLog"
participant Eng as "TTS Engine"
participant Post as "ITtsAudioPostProcessor"
User->>Handler : Initiate cloning
Handler->>Consent : Verify consent
Consent-->>Handler : Consent granted/denied
alt Granted
Handler->>Eng : Train/apply voice profile
Eng-->>Handler : Cloned voice artifacts
Handler->>Audit : Log cloning action
Handler->>Post : Post-process artifacts
Handler-->>User : Provide cloned voice pack
else Denied
Handler-->>User : Abort with reason
end
```

**Diagram sources**
- [Trackdub.Contracts/ISpeakerConsentService.cs](file://src/Trackdub.Contracts/ISpeakerConsentService.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Inference.Onnx/Kokoro/](file://src/Trackdub.Inference.Onnx/Kokoro)
- [Trackdub.Inference.Onnx/Qwen3Tts/](file://src/Trackdub.Inference.Onnx/Qwen3Tts)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)

**Section sources**
- [Trackdub.Contracts/ISpeakerConsentService.cs](file://src/Trackdub.Contracts/ISpeakerConsentService.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)

### Real-Time Synthesis Testing
- Streams audio chunks to the preview transport for immediate playback.
- Updates waveform and metrics in real time to guide parameter adjustments.
- Supports interruptible sessions to cancel ongoing synthesis.

```mermaid
sequenceDiagram
participant User as "User"
participant CLI as "CLI Interactive"
participant Handler as "GenerateCandidatesHandler"
participant Eng as "TTS Engine"
participant Preview as "IAudioPreviewTransport"
User->>CLI : Start real-time test
CLI->>Handler : Begin streaming synthesis
Handler->>Eng : Generate chunked audio
Eng-->>Handler : Stream chunks
Handler->>Preview : Forward chunks
Handler-->>CLI : Update progress & metrics
User->>CLI : Stop test
CLI->>Handler : Cancel session
Handler-->>Eng : Terminate synthesis
```

**Diagram sources**
- [Trackdub.Cli/Interactive/](file://src/Trackdub.Cli/Interactive)
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)

**Section sources**
- [Trackdub.Cli/Interactive/](file://src/Trackdub.Cli/Interactive)
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)

### Batch Voice Application
- Applies selected voice and parameters across multiple segments or projects.
- Uses batch options to control concurrency, output paths, and reporting.
- Integrates with preset store for reusable configurations.

```mermaid
flowchart TD
Start(["Batch Job"]) --> LoadOptions["Load BatchOptions"]
LoadOptions --> ResolvePresets["Resolve PipelinePreset"]
ResolvePresets --> PrepareSegments["Prepare Segment List"]
PrepareSegments --> Iterate{"For Each Segment"}
Iterate --> Synthesize["Run Synthesis with Params"]
Synthesize --> PostProcess["Post-Process Output"]
PostProcess --> RecordOutcome["Record Outcome"]
RecordOutcome --> Iterate
Iterate --> |Done| Report["Generate Batch Report"]
Report --> End(["Complete"])
```

**Diagram sources**
- [Trackdub.Sdk/BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [Trackdub.Sdk/BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [Trackdub.Sdk/PresetStore.cs](file://src/Trackdub.Sdk/PresetStore.cs)
- [Trackdub.Sdk/PipelinePreset.cs](file://src/Trackdub.Sdk/PipelinePreset.cs)

**Section sources**
- [Trackdub.Sdk/BatchProcessor.cs](file://src/Trackdub.Sdk/BatchProcessor.cs)
- [Trackdub.Sdk/BatchOptions.cs](file://src/Trackdub.Sdk/BatchOptions.cs)
- [Trackdub.Sdk/PresetStore.cs](file://src/Trackdub.Sdk/PresetStore.cs)
- [Trackdub.Sdk/PipelinePreset.cs](file://src/Trackdub.Sdk/PipelinePreset.cs)

### Template Usage
- Templates encapsulate common voice and parameter sets for quick adoption.
- Stored as presets and can be extended or overridden per project.
- Facilitates consistency across teams and projects.

```mermaid
flowchart TD
Start(["Template Selection"]) --> LoadTemplates["Load Preset Store"]
LoadTemplates --> ChooseTemplate["Choose Template"]
ChooseTemplate --> ApplyDefaults["Apply Default Parameters"]
ApplyDefaults --> Override{"Override Needed?"}
Override --> |Yes| EditParams["Edit Parameters"]
Override --> |No| Confirm["Confirm Template"]
EditParams --> Confirm
Confirm --> SaveAsCustom["Save as Custom Preset"]
SaveAsCustom --> UseInJob["Use in Batch/Session"]
```

**Section sources**
- [Trackdub.Sdk/PresetStore.cs](file://src/Trackdub.Sdk/PresetStore.cs)
- [Trackdub.Sdk/PipelinePreset.cs](file://src/Trackdub.Sdk/PipelinePreset.cs)

### Custom Voice Pack Integration
- Imports external voice packs into the model inventory.
- Validates format and metadata before registration.
- Supports versioning and rollback for managed updates.

```mermaid
flowchart TD
Start(["Import Voice Pack"]) --> DetectFormat["Detect Format & Metadata"]
DetectFormat --> Validate{"Valid?"}
Validate --> |No| Reject["Reject with Error"]
Validate --> |Yes| Register["Register in Model Inventory"]
Register --> Index["Index for Discovery"]
Index --> Ready["Available in Catalog"]
```

**Section sources**
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/IModelDownloadOrchestrator.cs](file://src/Trackdub.Contracts/IModelDownloadOrchestrator.cs)

## Dependency Analysis
The TTS subsystem depends on several cross-cutting services:
- Candidate group repository for data access
- Model inventory and download orchestrator for resource management
- Studio settings for user preferences
- Post-processing and enhancement services for audio quality
- Preview transport and waveform metrics for user feedback
- Consent and audit services for compliance

```mermaid
graph LR
Handler["GenerateCandidatesHandler"] --> Repo["ITtsCandidateGroupRepository"]
Handler --> Inv["IModelInventoryService"]
Handler --> Settings["IStudioSettingsService"]
Handler --> Post["ITtsAudioPostProcessor"]
Handler --> Enh["ISpeechAudioEnhancementService"]
Handler --> Preview["IAudioPreviewTransport"]
Handler --> Metrics["IWaveformSummaryGenerator"]
Handler --> Consent["ISpeakerConsentService"]
Handler --> Audit["IVoiceCloneAuditLog"]
```

**Diagram sources**
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)
- [Trackdub.Contracts/ISpeechAudioEnhancementService.cs](file://src/Trackdub.Contracts/ISpeechAudioEnhancementService.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)
- [Trackdub.Contracts/ISpeakerConsentService.cs](file://src/Trackdub.Contracts/ISpeakerConsentService.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)

**Section sources**
- [Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs](file://src/Trackdub.Application/Dubbing/GenerateCandidatesHandler.cs)
- [Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs](file://src/Trackdub.Contracts/Pipeline/ITtsCandidateGroupRepository.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/IStudioSettingsService.cs](file://src/Trackdub.Contracts/IStudioSettingsService.cs)
- [Trackdub.Contracts/ITtsAudioPostProcessor.cs](file://src/Trackdub.Contracts/ITtsAudioPostProcessor.cs)
- [Trackdub.Contracts/ISpeechAudioEnhancementService.cs](file://src/Trackdub.Contracts/ISpeechAudioEnhancementService.cs)
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)
- [Trackdub.Contracts/ISpeakerConsentService.cs](file://src/Trackdub.Contracts/ISpeakerConsentService.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)

## Performance Considerations
- Prefer GPU-accelerated execution providers where available for faster synthesis.
- Cache frequently used voice profiles and templates to reduce startup latency.
- Stream previews incrementally to minimize perceived delay.
- Limit batch concurrency based on hardware capacity to avoid memory pressure.
- Use lightweight models for real-time testing and heavier models for final exports.

[No sources needed since this section provides general guidance]

## Troubleshooting Guide
- If previews fail to play, verify the audio preview transport backend and device availability.
- For synthesis errors, check engine readiness and model inventory status; re-download if necessary.
- When cloning fails, confirm speaker consent and inspect the audit log for details.
- Use diagnostics bundle exporter to collect logs and artifacts for support.
- Validate waveform metrics to identify issues like clipping or low loudness.

**Section sources**
- [Trackdub.Contracts/IAudioPreviewTransport.cs](file://src/Trackdub.Contracts/IAudioPreviewTransport.cs)
- [Trackdub.Contracts/IModelInventoryService.cs](file://src/Trackdub.Contracts/IModelInventoryService.cs)
- [Trackdub.Contracts/IVoiceCloneAuditLog.cs](file://src/Trackdub.Contracts/IVoiceCloneAuditLog.cs)
- [Trackdub.Contracts/IDiagnosticsBundleExporter.cs](file://src/Trackdub.Contracts/IDiagnosticsBundleExporter.cs)
- [Trackdub.Contracts/IWaveformSummaryGenerator.cs](file://src/Trackdub.Contracts/IWaveformSummaryGenerator.cs)

## Conclusion
The voice selection and TTS configuration system integrates domain models, contracts, application handlers, inference engines, infrastructure services, and media utilities to deliver a robust, auditable, and user-friendly workflow. By leveraging previews, quality metrics, and batch capabilities, users can efficiently explore voices, fine-tune parameters, and produce high-quality synthesized audio at scale.

[No sources needed since this section summarizes without analyzing specific files]

## Appendices
- Benchmark runner demonstrates performance evaluation across engines and configurations.
- Starter packs provide curated model sets for rapid onboarding.

**Section sources**
- [Trackdub.Benchmarks/DubbingBenchmarkRunner.cs](file://src/Trackdub.Benchmarks/DubbingBenchmarkRunner.cs)
- [Trackdub.Composition/StarterPacks/](file://src/Trackdub.Composition/StarterPacks)