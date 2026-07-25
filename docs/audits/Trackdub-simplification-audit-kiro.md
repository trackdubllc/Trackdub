# Trackdub Simplification & Modernization Audit

**Auditor:** Kiro (manual source inspection)  
**Date:** July 2026  
**Branch:** `main`  
**Target:** .NET 10 / C# 13 (`LangVersion=latest`, some projects `preview`)

---

## Methodology

This audit was conducted by direct source code inspection, pattern searching, and architectural analysis -- not from ReSharper XML output. Focus areas:

1. Async/await anti-patterns (deadlock risk, thread blocking)
2. Nullability fragility
3. C# modernization opportunities
4. Architecture smells and structural complexity

Findings are ranked by **risk** (could cause bugs/deadlocks) and **impact** (widespread pattern vs. isolated instance).

---

## 1. Sync-over-Async (RISK: deadlock / thread starvation)

These block threads waiting on async results. In UI or ASP.NET contexts, this risks deadlocks.

| Severity | File | Line | Pattern | Risk |
|----------|------|------|---------|------|
| **High** | `Application/Licensing/ExportTierGate.cs` | property getter | `InitializeAsync().GetAwaiter().GetResult()` | Deadlock if called from UI SynchronizationContext |
| **High** | `Inference.Onnx/QwenAssistant/QwenLocalAssistantEngine.cs` | `IsAvailable` property | `PlanAsync().GetAwaiter().GetResult()` | Property triggers async work synchronously; deadlock risk in UI |
| **Medium** | `Infrastructure/Persistence/Repositories/LocalModelCacheRecordLookup.cs` | sync interface impl | `LoadAsync().GetAwaiter().GetResult()` | Blocks on every model lookup; interface should be async |
| **Medium** | `Inference.Onnx/Kokoro/KokoroVoiceCatalog.cs` | `Load()` static method | `LoadAsync(...).ConfigureAwait(false).GetAwaiter().GetResult()` | ConfigureAwait mitigates deadlock but still blocks thread |
| **Low** | `Inference.Onnx/Kokoro/EspeakNgPhonemizer.cs` | after `WaitForExit` | `readTask.GetAwaiter().GetResult()` | Process already exited so task is completed; still, method could be fully async |
| **Low** | `Media.Playback/LibMpvWindowsBootstrap.cs` | bootstrap path | `.GetAwaiter().GetResult()` | One-time startup; ConfigureAwait(false) used |
| **Low** | `Sdk/HeadlessDubbingSessionFactory.cs` | factory init | `.GetAwaiter().GetResult()` | Headless CLI context, no SyncContext; acceptable |

**Recommended fix:** Make `ExportTierGate` and `QwenLocalAssistantEngine.IsAvailable` truly async (change property to `Task<bool>` method or use lazy async initialization). For `LocalModelCacheRecordLookup`, make the interface async.

---

## 2. Fire-and-Forget without Error Handling

Discarded tasks where exceptions vanish silently.

| File | Pattern | Risk |
|------|---------|------|
| `App.Avalonia/Services/ProjectLoadCoordinator.cs` | `_ = LoadProjectInternalAsync(...)` | Swallowed exceptions on project load |
| `App.Avalonia/Playback/AvaloniaPlaybackComposition.cs` | `_ = PrewarmAsync()` | Silent failure on playback native bootstrap |
| `App.Avalonia/ViewModels/AvaloniaMainWindowViewModel.*.cs` | Multiple `_ = SomeAsync()` in VM code | Standard Avalonia pattern, but needs `.ContinueWith(t => Log(t.Exception))` or equivalent |

**Note:** Fire-and-forget in Avalonia ViewModels is common (you can't await from a property change handler). The fix is adding `.ContinueWith(t => ..., TaskContinuationOptions.OnlyOnFaulted)` or using a centralized error handler.

---

## 3. Fragile Null-Forgiving Operator (!) Patterns

Places where `!` is used on fields/properties that are nullable due to partial initialization, creating hidden NRE risk if initialization order changes.

| Severity | File | Pattern | Risk |
|----------|------|---------|------|
| **High** | `ViewModels/AvaloniaMainWindowViewModel.cs` | `ExportMix!`, `_operationRunner!`, `settingsService!`, `projectSession!` | Nullable fields used with `!` assuming prior initialization; fragile if startup order changes |
| **Medium** | `Inference.Onnx/Qwen3Tts/Qwen3TtsEngine.cs` | `request.VoiceCloneReference!.ReferenceTranscript!` | Second `!` is 37 lines after the null guard; fragile if code reorders |
| **Medium** | `Media.Playback/LibMpvCompositedPlaybackBackend.cs` | `mpv_create!()`, `mpv_initialize!()`, etc. | Native function pointers declared nullable, invoked with `!` at every call site instead of guarding once at load |

**Recommended fix:** For AvaloniaMainWindowViewModel, use `[MemberNotNull]` attributes or extract required services into a non-nullable initialization record. For LibMpv, validate all function pointers at load time and throw, then store as non-nullable.

---

## 4. Redundant Defensive Checks

`ArgumentNullException.ThrowIfNull` on parameters that are already non-nullable (with `Nullable=enable`):

| File | Parameter |
|------|-----------|
| `Application/Projects/SegmentStageRunProvenanceStore.cs` | `IReadOnlyList<int> allSegmentIndices` |
| `Application/Projects/ProjectMediaIngestService.cs` | various non-nullable params |
| `Application/Transcripts/SubtitleExportService.cs` | various non-nullable params |

**Note:** This is a style debate. With `Nullable=enable`, the compiler prevents null at call sites. ThrowIfNull adds runtime defense for callers that suppress warnings. Low priority but adds noise.

---

## 5. `System.Threading.Lock` Migration (20+ instances)

.NET 9+ introduced `System.Threading.Lock` which is more efficient than `lock(object)`. The project targets net10.0.

**Top candidates:**

| File | Field | Usage |
|------|-------|-------|
| `App.Avalonia/Playback/AvaloniaVideoFramePresenter.cs:15` | `private readonly object sync` | lock(sync) on lines 47/57/80/109/125 |
| `Inference.Onnx/OnnxExecutionSessionFactory.cs:35` | `private static readonly object _initLock` | Static lock for EP initialization |
| `App.Avalonia/Services/VoiceCloneConsentCoordinator.cs:7` | `private readonly object gate` | Consent coordination |
| `Application/Licensing/ExportTierGate.cs:14` | `private readonly object InitGate` | License init gate |
| `Application/Transcripts/TranscriptWorkspace.cs:29` | `private readonly object disposalSync` | Disposal guard |
| `Inference.Onnx/Pool/OnnxSessionPool.cs` | lock fields | Session pool management |
| `Media.Playback/LibMpvCompositedPlaybackBackend.cs` | multiple lock objects | Playback state |

**Fix:** Replace `private readonly object x = new();` with `private readonly Lock x = new();` -- drop-in replacement, better JIT optimization.

---

## 6. Non-Sealed Classes (Design Issue)

Classes without virtual members or inheritance intent should be `sealed` per project conventions. Unsealed classes:
- Prevent devirtualization optimizations
- For IDisposable: create GC finalization overhead

| File | Class | Issue |
|------|-------|-------|
| `Application/Services/ProjectService.cs` | `ProjectService` | No virtual members, not inherited |
| `Application/Services/SessionService.cs` | `SessionService` | No virtual members |
| `Application/Services/TranslationService.cs` | `TranslationService` | No virtual members |
| `Application/Services/VoiceAssignmentService.cs` | `VoiceAssignmentService` | No virtual members |
| `App.Avalonia/ViewModels/Dev/DevLogViewModel.cs` | `DevLogViewModel : IDisposable` | **IDisposable without sealed** -- GC perf concern |
| `Inference.Onnx/Qwen3Tts/Pipeline/QwenTtsOptions.cs` | `QwenTtsOptions` | Mutable options bag, no inheritance |
| `Inference.Onnx/Qwen3Tts/Pipeline/TextToSpeechOptions.cs` | `TextToSpeechOptions` | Same |

**Fix:** Add `sealed` keyword. For `DevLogViewModel`, seal or add a destructor suppression.

---

## 7. Duplicated Constants

| Location A | Location B | What's duplicated |
|------------|------------|-------------------|
| `Inference.Onnx/Qwen3Asr/Qwen3AsrPromptTokens.cs` | `Inference.Onnx/Qwen3Tts/` token constants | Token IDs: EndOfText=151643, ImStart=151644, ImEnd=151645, AudioStart=151669, AudioEnd=151670 |

**Fix:** Extract to shared `Qwen3SharedTokens` constant class in `Inference.Onnx/Qwen3/` or a shared location both ASR and TTS reference.

---

## 8. Architecture Smells

### 8a. God Class: AvaloniaMainWindowViewModel

- **Main file:** 2386 lines
- **Partial files:** 16+ (`PipelineUi`, `Panels`, `SegmentEdit`, `History`, `PreviewMix`, `ProjectLoad`, `ProjectImport`, `GlossaryHighlights`, `PipelineStageExecutionHost`, `SpeakerVoice`, `SegmentPipeline`, `SidecarCommands`, `StudioSettings`, `Subtitles`, `Timeline`, `Waveform`)
- **Injected dependencies:** 15+
- **Service locator usage:** Resolves 5+ services from `IServiceScopeFactory` at runtime

The partial file split helps readability but doesn't address the coupling. This VM is the coordinator for nearly all UI state.

**Recommendation:** Not actionable as a simplification (it's a known architectural debt). Document as a candidate for extraction into focused coordinators if/when the left-panel pipeline UX redesign lands.

---

### 8b. Service Locator in ViewModels

Multiple ViewModel partial files resolve services at runtime via `IServiceScopeFactory`:

```csharp
using var scope = _scopeFactory.CreateScope();
var service = scope.ServiceProvider.GetRequiredService<IModelInventoryService>();
```

Found in: `PipelineUi`, `GlossaryHighlights`, `SegmentEdit`, `SpeakerVoice`, `StudioSettings`

**Why it exists:** Lazy resolution avoids circular dependencies and keeps ctor injection manageable on the god-class VM.

**Recommendation:** Accept as pragmatic trade-off until VM extraction reduces dependency count. Not a simplification candidate today.

---

### 8c. Namespace Density: `Trackdub.Application.Transcripts`

~80+ files in a single namespace. Types range from stage handlers to DTOs to workflows to services.

**Recommendation:** Split into sub-namespaces when next refactoring this area:
- `Transcripts.Stages/` (stage handlers)
- `Transcripts.Models/` (DTOs, contracts)
- `Transcripts.Workflows/` (orchestration)

---

### 8d. Duplicated Stage Handler Boilerplate

Every stage handler follows an identical pattern:
1. Check prerequisites
2. Call `StageRunHelper.StartKnownStageRun()`
3. Execute core logic
4. Write artifacts
5. Complete stage run

`StageRunHelper.StartKnownStageRun` has an exhaustive switch on stage names that must grow with every new stage. All branches call the same factory method.

**Recommendation:** Consider a base class or pipeline middleware pattern to eliminate the ceremony. The switch statement can be replaced with a dictionary or attribute-based registration.

---

## 9. Byte Array Allocation Opportunities (Span/stackalloc)

| File | Current | Opportunity |
|------|---------|-------------|
| `Inference.Onnx/Qwen3Tts/Models/NpyReader.cs:91` | `byte[] headerBytes = new byte[10]` | `Span<byte> headerBytes = stackalloc byte[10]` (small fixed-size header read) |
| `Media/Waveforms/WavePcm16.cs` | Various `byte[]` for WAV header parsing | stackalloc for 44-byte WAV headers |
| `Media/Extraction/Pcm16WaveClipExtractor.cs` | Buffer allocations for audio chunks | ArrayPool<byte>.Shared for large buffers |

**Impact:** Low-medium. Only matters in hot paths (batch TTS, waveform generation).

---

## 10. Quick Wins (High confidence, mechanical)

These can be applied in bulk with minimal review:

| Category | Count | Fix |
|----------|-------|-----|
| `lock(object)` to `System.Threading.Lock` | ~20 | Drop-in replacement |
| Add `sealed` to leaf classes | ~10-15 in Application/Inference | Add keyword |
| Redundant `!` after confirmed non-null | ~15-20 in App.Avalonia | Remove operator |
| `new List<T>()` to `[]` where target-typed | ~40+ | Collection expression |
| `Enumerable.Empty<T>()` to `Array.Empty<T>()` or `[]` | ~5 | Swap |

---

## Summary

| Category | Count | Risk | Effort |
|----------|-------|------|--------|
| Sync-over-async (potential deadlocks) | 7 | **High** | Medium (interface changes needed) |
| Fire-and-forget without error handling | ~10 | Medium | Low (add continuation) |
| Fragile `!` patterns | ~20 | Medium | Low-Medium |
| `System.Threading.Lock` migration | ~20 | None (improvement) | Low |
| Non-sealed classes | ~10 | Low (perf) | Trivial |
| Duplicated Qwen3 tokens | 1 instance | Low | Trivial |
| God class / service locator | 1 class | Architectural | High (not a simplification task) |
| Namespace density | 1 namespace | Organizational | Medium |
| Span/stackalloc opportunities | ~5 | None (perf) | Low |
| Collection expression modernization | ~40 | None (style) | Low |

---

## Recommended Priority

### P0 -- Fix Now (Risk of bugs)
1. `ExportTierGate` sync-over-async in property getter -- potential deadlock
2. `QwenLocalAssistantEngine.IsAvailable` sync-over-async -- same

### P1 -- Fix Soon (Quality)
3. `System.Threading.Lock` migration (20 instances) -- free perf
4. Seal leaf classes in Application (4-5 classes)
5. Seal `DevLogViewModel` (IDisposable concern)
6. Extract shared Qwen3 token constants

### P2 -- Fix When Touching (Cleanup)
7. Remove redundant `!` operators where nullable field is always initialized
8. LibMpv function pointer validation at load time (remove per-call `!`)
9. `LocalModelCacheRecordLookup` interface to async
10. Collection expressions, Span opportunities, redundant ThrowIfNull

### P3 -- Track (Architectural)
11. AvaloniaMainWindowViewModel decomposition (when pipeline UX redesign lands)
12. `Application.Transcripts` namespace split
13. Stage handler boilerplate reduction
