# Transcript Editing & Alignment

<cite>
**Referenced Files in This Document**
- [TranscriptWorkspaceContext.cs](file://src/Trackdub.Composition/TranscriptWorkspaceContext.cs)
- [TranscriptWorkspaceFactory.cs](file://src/Trackdub.Composition/TranscriptWorkspaceFactory.cs)
- [TranscriptWorkspaceSession.cs](file://src/Trackdub.Composition/TranscriptWorkspaceSession.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)
- [ProportionalTranslatedWordAlignmentService.cs](file://src/Trackdub.Application/Transcripts/ProportionalTranslatedWordAlignmentService.cs)
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)
- [IExportServices.cs](file://src/Trackdub.Contracts/IExportServices.cs)
- [ITranscriptWorkspaceContext.cs](file://src/Trackdub.Contracts/ITranscriptWorkspaceContext.cs)
- [TranscriptSegment.cs](file://src/Trackdub.Domain/Transcript/TranscriptSegment.cs)
- [AudioTimestamps.cs](file://src/Trackdub.Media/Timing/AudioTimestamps.cs)
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)
- [TranscriptEditorViewModel.cs](file://src/Trackdub.Application/Transcripts/TranscriptEditorViewModel.cs)
- [SegmentManipulationService.cs](file://src/Trackdub.Application/Transcripts/SegmentManipulationService.cs)
- [TextCorrectionWorkflow.cs](file://src/Trackdub.Application/Transcripts/TextCorrectionWorkflow.cs)
- [AlignmentRefinementEngine.cs](file://src/Trackdub.Application/Transcripts/AlignmentRefinementEngine.cs)
- [ManualAdjustmentTool.cs](file://src/Trackdub.Application/Transcripts/ManualAdjustmentTool.cs)
- [TimestampPrecisionControl.cs](file://src/Trackdub.Application/Transcripts/TimestampPrecisionControl.cs)
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
This document explains Trackdub’s transcript editing and alignment features with a focus on the forced alignment engine, phoneme timing calculation, and frame-level synchronization with video. It covers the transcript editing interface, segment manipulation tools, text correction workflows, automatic alignment refinement, manual adjustment capabilities, and timestamp precision control. It also details how transcript segments relate to audio timestamps and video frames, provides guidance for improving alignment accuracy, handling overlapping speech and background noise, and documents export formats, import capabilities, and integration with external editing tools.

## Project Structure
The transcript and alignment functionality spans multiple layers:
- Composition layer wires workspace contexts and sessions for transcript editing.
- Application layer implements alignment refinement, phoneme timing, proportional word alignment, subtitle export, and editor view models.
- Domain layer defines core entities like transcript segments.
- Media layer provides audio timestamps and video frame synchronization utilities.
- Inference layer hosts the forced alignment service that aligns transcripts to audio.

```mermaid
graph TB
subgraph "Composition"
TWC["TranscriptWorkspaceContext"]
TWFactory["TranscriptWorkspaceFactory"]
TWSession["TranscriptWorkspaceSession"]
end
subgraph "Application"
EditorVM["TranscriptEditorViewModel"]
SegMan["SegmentManipulationService"]
TextCorr["TextCorrectionWorkflow"]
AlignRef["AlignmentRefinementEngine"]
ManualAdj["ManualAdjustmentTool"]
TimePrec["TimestampPrecisionControl"]
PhonPlan["PhonemeTimingPlanner"]
PropAlign["ProportionalTranslatedWordAlignmentService"]
SubExp["SubtitleExportService"]
end
subgraph "Inference"
FA["ForcedAlignmentService"]
end
subgraph "Domain"
TSeg["TranscriptSegment"]
end
subgraph "Media"
AT["AudioTimestamps"]
VFS["VideoFrameSync"]
end
TWC --> EditorVM
TWFactory --> TWC
TWSession --> TWC
EditorVM --> SegMan
EditorVM --> TextCorr
EditorVM --> AlignRef
EditorVM --> ManualAdj
EditorVM --> TimePrec
AlignRef --> FA
PhonPlan --> FA
PropAlign --> FA
SegMan --> TSeg
SubExp --> TSeg
SubExp --> AT
SubExp --> VFS
```

**Diagram sources**
- [TranscriptWorkspaceContext.cs](file://src/Trackdub.Composition/TranscriptWorkspaceContext.cs)
- [TranscriptWorkspaceFactory.cs](file://src/Trackdub.Composition/TranscriptWorkspaceFactory.cs)
- [TranscriptWorkspaceSession.cs](file://src/Trackdub.Composition/TranscriptWorkspaceSession.cs)
- [TranscriptEditorViewModel.cs](file://src/Trackdub.Application/Transcripts/TranscriptEditorViewModel.cs)
- [SegmentManipulationService.cs](file://src/Trackdub.Application/Transcripts/SegmentManipulationService.cs)
- [TextCorrectionWorkflow.cs](file://src/Trackdub.Application/Transcripts/TextCorrectionWorkflow.cs)
- [AlignmentRefinementEngine.cs](file://src/Trackdub.Application/Transcripts/AlignmentRefinementEngine.cs)
- [ManualAdjustmentTool.cs](file://src/Trackdub.Application/Transcripts/ManualAdjustmentTool.cs)
- [TimestampPrecisionControl.cs](file://src/Trackdub.Application/Transcripts/TimestampPrecisionControl.cs)
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)
- [ProportionalTranslatedWordAlignmentService.cs](file://src/Trackdub.Application/Transcripts/ProportionalTranslatedWordAlignmentService.cs)
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [TranscriptSegment.cs](file://src/Trackdub.Domain/Transcript/TranscriptSegment.cs)
- [AudioTimestamps.cs](file://src/Trackdub.Media/Timing/AudioTimestamps.cs)
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)

**Section sources**
- [TranscriptWorkspaceContext.cs](file://src/Trackdub.Composition/TranscriptWorkspaceContext.cs)
- [TranscriptWorkspaceFactory.cs](file://src/Trackdub.Composition/TranscriptWorkspaceFactory.cs)
- [TranscriptWorkspaceSession.cs](file://src/Trackdub.Composition/TranscriptWorkspaceSession.cs)
- [TranscriptEditorViewModel.cs](file://src/Trackdub.Application/Transcripts/TranscriptEditorViewModel.cs)
- [SegmentManipulationService.cs](file://src/Trackdub.Application/Transcripts/SegmentManipulationService.cs)
- [TextCorrectionWorkflow.cs](file://src/Trackdub.Application/Transcripts/TextCorrectionWorkflow.cs)
- [AlignmentRefinementEngine.cs](file://src/Trackdub.Application/Transcripts/AlignmentRefinementEngine.cs)
- [ManualAdjustmentTool.cs](file://src/Trackdub.Application/Transcripts/ManualAdjustmentTool.cs)
- [TimestampPrecisionControl.cs](file://src/Trackdub.Application/Transcripts/TimestampPrecisionControl.cs)
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)
- [ProportionalTranslatedWordAlignmentService.cs](file://src/Trackdub.Application/Transcripts/ProportionalTranslatedWordAlignmentService.cs)
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [TranscriptSegment.cs](file://src/Trackdub.Domain/Transcript/TranscriptSegment.cs)
- [AudioTimestamps.cs](file://src/Trackdub.Media/Timing/AudioTimestamps.cs)
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)

## Core Components
- Forced Alignment Engine: Aligns transcript text to audio at phoneme or word boundaries using acoustic modeling and dynamic time warping.
- Phoneme Timing Planner: Computes per-phoneme durations and refines segment timings based on linguistic cues.
- Frame-Level Sync: Converts audio timestamps to video frames using frame rate and sample rate conversions.
- Transcript Editor UI: Provides segment creation, splitting, merging, and text editing with live preview.
- Segment Manipulation Tools: Split, merge, trim, reorder, and adjust segment boundaries.
- Text Correction Workflow: Applies language-aware corrections and glossary terms while preserving alignment.
- Alignment Refinement: Iteratively improves alignment by re-running forced alignment on edited segments.
- Manual Adjustment Tool: Fine-grained boundary dragging with snap-to-events and confidence-based hints.
- Timestamp Precision Control: Configurable rounding and snapping to milliseconds or frames.

**Section sources**
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)
- [TranscriptEditorViewModel.cs](file://src/Trackdub.Application/Transcripts/TranscriptEditorViewModel.cs)
- [SegmentManipulationService.cs](file://src/Trackdub.Application/Transcripts/SegmentManipulationService.cs)
- [TextCorrectionWorkflow.cs](file://src/Trackdub.Application/Transcripts/TextCorrectionWorkflow.cs)
- [AlignmentRefinementEngine.cs](file://src/Trackdub.Application/Transcripts/AlignmentRefinementEngine.cs)
- [ManualAdjustmentTool.cs](file://src/Trackdub.Application/Transcripts/ManualAdjustmentTool.cs)
- [TimestampPrecisionControl.cs](file://src/Trackdub.Application/Transcripts/TimestampPrecisionControl.cs)

## Architecture Overview
The system composes a transcript workspace context that exposes services to the editor view model. The editor orchestrates segment manipulation, text correction, and alignment refinement. The forced alignment service performs the heavy lifting, while phoneme timing planning and proportional word alignment refine results. Export services convert aligned segments into standard subtitle formats and synchronize with video frames.

```mermaid
sequenceDiagram
participant User as "User"
participant Editor as "TranscriptEditorViewModel"
participant SegMan as "SegmentManipulationService"
participant TextCorr as "TextCorrectionWorkflow"
participant AlignRef as "AlignmentRefinementEngine"
participant FA as "ForcedAlignmentService"
participant Phon as "PhonemeTimingPlanner"
participant Export as "SubtitleExportService"
User->>Editor : Edit transcript / adjust segments
Editor->>SegMan : Split/Merge/Trim segments
SegMan-->>Editor : Updated segment list
Editor->>TextCorr : Apply text corrections
TextCorr-->>Editor : Corrected text + constraints
Editor->>AlignRef : Trigger alignment refinement
AlignRef->>FA : Run forced alignment on segments
FA-->>AlignRef : Phoneme/word alignments
AlignRef->>Phon : Compute phoneme timings
Phon-->>AlignRef : Refined timings
AlignRef-->>Editor : Finalized timestamps
Editor->>Export : Export subtitles (SRT/VTT)
Export-->>User : Downloaded file
```

**Diagram sources**
- [TranscriptEditorViewModel.cs](file://src/Trackdub.Application/Transcripts/TranscriptEditorViewModel.cs)
- [SegmentManipulationService.cs](file://src/Trackdub.Application/Transcripts/SegmentManipulationService.cs)
- [TextCorrectionWorkflow.cs](file://src/Trackdub.Application/Transcripts/TextCorrectionWorkflow.cs)
- [AlignmentRefinementEngine.cs](file://src/Trackdub.Application/Transcripts/AlignmentRefinementEngine.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)

## Detailed Component Analysis

### Forced Alignment Engine
The forced alignment engine maps transcript tokens to audio frames using acoustic models and dynamic programming. It supports word-level and phoneme-level segmentation and can be re-run on edited segments to maintain consistency.

```mermaid
flowchart TD
Start(["Start Alignment"]) --> Prep["Prepare Audio Features"]
Prep --> Tokenize["Tokenize Transcript"]
Tokenize --> ModelRun["Run Acoustic Model"]
ModelRun --> DTW["Dynamic Time Warping"]
DTW --> Scores{"Confidence Check"}
Scores --> |Low| Realign["Realign with Expanded Search"]
Realign --> Scores
Scores --> |High| Output["Emit Segment Timings"]
Output --> End(["End"])
```

**Diagram sources**
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)

**Section sources**
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)

### Phoneme Timing Calculation
Phoneme timing planner computes per-phoneme durations and adjusts segment boundaries to respect linguistic constraints. It integrates with the forced alignment output to refine timings at a finer granularity.

```mermaid
classDiagram
class PhonemeTimingPlanner {
+ComputePhonemeDurations(segments)
+RefineBoundaries(alignments)
+ApplyLinguisticConstraints()
}
class ForcedAlignmentService {
+Align(transcript, audio)
+GetPhonemeScores()
}
PhonemeTimingPlanner --> ForcedAlignmentService : "uses"
```

**Diagram sources**
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)

**Section sources**
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)

### Frame-Level Synchronization
Frame-level sync converts audio timestamps to video frames using frame rate and sample rate conversions. It ensures precise alignment between transcript segments and visual frames.

```mermaid
flowchart TD
AStart(["Audio Timestamp"]) --> Convert["Convert ms to samples"]
Convert --> Rate["Apply Sample Rate Conversion"]
Rate --> Frames["Map to Video Frames"]
Frames --> Round["Round to Nearest Frame"]
Round --> AEnd(["Frame Index"])
```

**Diagram sources**
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)
- [AudioTimestamps.cs](file://src/Trackdub.Media/Timing/AudioTimestamps.cs)

**Section sources**
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)
- [AudioTimestamps.cs](file://src/Trackdub.Media/Timing/AudioTimestamps.cs)

### Transcript Editing Interface
The editor view model provides an interactive interface for creating, editing, and managing transcript segments. It integrates with segment manipulation tools and text correction workflows.

```mermaid
classDiagram
class TranscriptEditorViewModel {
+Segments : TranscriptSegment[]
+EditSegment(id, text)
+SplitSegment(id, position)
+MergeSegments(ids)
+PreviewPlayback()
}
class SegmentManipulationService {
+Split(segment, position)
+Merge(segments)
+Trim(segment, startMs, endMs)
}
class TextCorrectionWorkflow {
+ApplyGlossary(text, glossary)
+CorrectSpelling(text)
+PreserveAlignment()
}
TranscriptEditorViewModel --> SegmentManipulationService : "uses"
TranscriptEditorViewModel --> TextCorrectionWorkflow : "uses"
```

**Diagram sources**
- [TranscriptEditorViewModel.cs](file://src/Trackdub.Application/Transcripts/TranscriptEditorViewModel.cs)
- [SegmentManipulationService.cs](file://src/Trackdub.Application/Transcripts/SegmentManipulationService.cs)
- [TextCorrectionWorkflow.cs](file://src/Trackdub.Application/Transcripts/TextCorrectionWorkflow.cs)
- [TranscriptSegment.cs](file://src/Trackdub.Domain/Transcript/TranscriptSegment.cs)

**Section sources**
- [TranscriptEditorViewModel.cs](file://src/Trackdub.Application/Transcripts/TranscriptEditorViewModel.cs)
- [SegmentManipulationService.cs](file://src/Trackdub.Application/Transcripts/SegmentManipulationService.cs)
- [TextCorrectionWorkflow.cs](file://src/Trackdub.Application/Transcripts/TextCorrectionWorkflow.cs)
- [TranscriptSegment.cs](file://src/Trackdub.Domain/Transcript/TranscriptSegment.cs)

### Automatic Alignment Refinement
The alignment refinement engine iteratively improves alignment by re-running forced alignment on edited segments and applying phoneme timing adjustments.

```mermaid
sequenceDiagram
participant Editor as "Editor"
participant Refine as "AlignmentRefinementEngine"
participant FA as "ForcedAlignmentService"
participant Phon as "PhonemeTimingPlanner"
Editor->>Refine : Request refinement
Refine->>FA : Re-align edited segments
FA-->>Refine : New alignments
Refine->>Phon : Compute phoneme timings
Phon-->>Refine : Refined durations
Refine-->>Editor : Updated timestamps
```

**Diagram sources**
- [AlignmentRefinementEngine.cs](file://src/Trackdub.Application/Transcripts/AlignmentRefinementEngine.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)

**Section sources**
- [AlignmentRefinementEngine.cs](file://src/Trackdub.Application/Transcripts/AlignmentRefinementEngine.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)

### Manual Adjustment Capabilities
The manual adjustment tool allows fine-grained boundary dragging with snap-to-events and confidence-based hints. It integrates with timestamp precision control for accurate adjustments.

```mermaid
flowchart TD
MStart(["Select Boundary"]) --> Drag["Drag Boundary"]
Drag --> Snap{"Snap to Event?"}
Snap --> |Yes| SnapEvent["Snap to Detected Event"]
Snap --> |No| Free["Free Adjustment"]
SnapEvent --> Precision["Apply Precision Control"]
Free --> Precision
Precision --> Validate{"Valid Range?"}
Validate --> |Yes| Apply["Apply Adjustment"]
Validate --> |No| Reject["Reject Adjustment"]
Apply --> MEnd(["Done"])
Reject --> MEnd
```

**Diagram sources**
- [ManualAdjustmentTool.cs](file://src/Trackdub.Application/Transcripts/ManualAdjustmentTool.cs)
- [TimestampPrecisionControl.cs](file://src/Trackdub.Application/Transcripts/TimestampPrecisionControl.cs)

**Section sources**
- [ManualAdjustmentTool.cs](file://src/Trackdub.Application/Transcripts/ManualAdjustmentTool.cs)
- [TimestampPrecisionControl.cs](file://src/Trackdub.Application/Transcripts/TimestampPrecisionControl.cs)

### Timestamp Precision Control
Timestamp precision control configures rounding and snapping to milliseconds or frames. It ensures consistent precision across all alignment operations.

```mermaid
classDiagram
class TimestampPrecisionControl {
+SetPrecision(msOrFrames)
+Round(timestamp)
+SnapToGrid(timestamp)
}
class ManualAdjustmentTool {
+AdjustBoundary(segment, delta)
}
TimestampPrecisionControl <.. ManualAdjustmentTool : "uses"
```

**Diagram sources**
- [TimestampPrecisionControl.cs](file://src/Trackdub.Application/Transcripts/TimestampPrecisionControl.cs)
- [ManualAdjustmentTool.cs](file://src/Trackdub.Application/Transcripts/ManualAdjustmentTool.cs)

**Section sources**
- [TimestampPrecisionControl.cs](file://src/Trackdub.Application/Transcripts/TimestampPrecisionControl.cs)
- [ManualAdjustmentTool.cs](file://src/Trackdub.Application/Transcripts/ManualAdjustmentTool.cs)

### Proportional Translated Word Alignment
The proportional translated word alignment service maintains alignment when translating text, ensuring that translated segments match the original timing structure.

```mermaid
classDiagram
class ProportionalTranslatedWordAlignmentService {
+AlignTranslated(originalSegments, translatedText)
+MaintainProportions()
+ValidateAlignment()
}
class ForcedAlignmentService {
+Align(transcript, audio)
}
ProportionalTranslatedWordAlignmentService --> ForcedAlignmentService : "uses"
```

**Diagram sources**
- [ProportionalTranslatedWordAlignmentService.cs](file://src/Trackdub.Application/Transcripts/ProportionalTranslatedWordAlignmentService.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)

**Section sources**
- [ProportionalTranslatedWordAlignmentService.cs](file://src/Trackdub.Application/Transcripts/ProportionalTranslatedWordAlignmentService.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)

### Export Formats and Import Capabilities
The subtitle export service supports standard formats like SRT and VTT, with options for frame-level precision and metadata preservation. Import capabilities allow loading existing transcripts from common formats.

```mermaid
flowchart TD
EStart(["Export Request"]) --> Format{"Format Selection"}
Format --> |SRT| GenSRT["Generate SRT"]
Format --> |VTT| GenVTT["Generate VTT"]
GenSRT --> FrameSync["Apply Frame Sync"]
GenVTT --> FrameSync
FrameSync --> Metadata["Add Metadata"]
Metadata --> EEnd(["Download File"])
```

**Diagram sources**
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)
- [IExportServices.cs](file://src/Trackdub.Contracts/IExportServices.cs)
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)

**Section sources**
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)
- [IExportServices.cs](file://src/Trackdub.Contracts/IExportServices.cs)
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)

## Dependency Analysis
The transcript editing and alignment system has clear dependency relationships:
- Composition layer depends on contracts and provides workspace contexts.
- Application layer depends on domain entities and media utilities.
- Inference layer provides alignment services used by application components.
- Export services depend on timing utilities and domain models.

```mermaid
graph TB
Contracts["Contracts"] --> Composition["Composition"]
Composition --> Application["Application"]
Domain["Domain"] --> Application
Media["Media"] --> Application
Inference["Inference"] --> Application
Application --> Export["Export Services"]
```

**Diagram sources**
- [ITranscriptWorkspaceContext.cs](file://src/Trackdub.Contracts/ITranscriptWorkspaceContext.cs)
- [TranscriptWorkspaceContext.cs](file://src/Trackdub.Composition/TranscriptWorkspaceContext.cs)
- [TranscriptSegment.cs](file://src/Trackdub.Domain/Transcript/TranscriptSegment.cs)
- [AudioTimestamps.cs](file://src/Trackdub.Media/Timing/AudioTimestamps.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)

**Section sources**
- [ITranscriptWorkspaceContext.cs](file://src/Trackdub.Contracts/ITranscriptWorkspaceContext.cs)
- [TranscriptWorkspaceContext.cs](file://src/Trackdub.Composition/TranscriptWorkspaceContext.cs)
- [TranscriptSegment.cs](file://src/Trackdub.Domain/Transcript/TranscriptSegment.cs)
- [AudioTimestamps.cs](file://src/Trackdub.Media/Timing/AudioTimestamps.cs)
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)

## Performance Considerations
- Forced alignment performance depends on audio length and model complexity; consider chunking long audio files.
- Phoneme timing calculations are CPU-intensive; parallel processing can improve throughput.
- Frame-level synchronization should use efficient conversions to avoid unnecessary recalculations.
- Export operations should batch subtitle generation for large projects.
- Memory usage increases with large transcripts; implement streaming where possible.

## Troubleshooting Guide
Common alignment issues and solutions:
- Poor alignment accuracy: Improve audio quality, reduce background noise, and ensure proper language model selection.
- Overlapping speech: Use speaker diarization before alignment and adjust search parameters.
- Background noise: Apply noise reduction preprocessing and increase confidence thresholds.
- Frame sync errors: Verify frame rate and sample rate settings match source media.
- Export format issues: Ensure timestamp precision matches target format requirements.

**Section sources**
- [ForcedAlignmentService.cs](file://src/Trackdub.Inference.Onnx/ForcedAlignment/ForcedAlignmentService.cs)
- [PhonemeTimingPlanner.cs](file://src/Trackdub.Application/Transcripts/PhonemeTimingPlanner.cs)
- [VideoFrameSync.cs](file://src/Trackdub.Media/Timing/VideoFrameSync.cs)
- [SubtitleExportService.cs](file://src/Trackdub.Application/Export/SubtitleExportService.cs)

## Conclusion
Trackdub’s transcript editing and alignment system provides a comprehensive solution for synchronizing text with audio and video. The forced alignment engine, phoneme timing planner, and frame-level synchronization work together to deliver precise alignments. The editing interface offers powerful tools for segment manipulation, text correction, and manual adjustments. With robust export capabilities and integration with external tools, it serves as a complete workflow for transcript editing and alignment tasks.

## Appendices
- Best practices for improving alignment accuracy
- Handling edge cases in speech recognition
- Integration guides for external editing tools
- Performance tuning recommendations