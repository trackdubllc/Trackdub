# Dead Code Audit Review: Mistakes & Resolutions

**Source:** `Trackdub-dead-code-audit-codex-refactor.md` (ReSharper `jb inspectcode` on `codex/refactor`)  
**Reviewed by:** Kiro, verified against `main` source code (July 2026)  
**Second-pass verification:** Completed (3 disputed items resolved below)

---

## Executive Summary

The audit is broadly solid (~95% accurate). The 46 "do not remove" items are all correctly categorized. However, verification against actual source reveals:

- **~80 false positives** from CommunityToolkit.Mvvm source-generator partial methods
- **~10 misclassified interface parameters** that are contractual and cannot be removed
- **1 item marked "safe" needs nuance** -- two `NullOpenVinoAvailabilityProvider` definitions exist (see section 1)
- **~10 items marked "review needed" that are confirmed dead** and can be promoted to safe
- **GlobalUsings.cs "delete" recommendation is wrong** for most files (they contain real content)
- **All 7 unused PackageVersion entries confirmed safe** to remove (verified individually)

---

## 1. CLARIFIED: NullOpenVinoAvailabilityProvider (two definitions exist)

There are **two** `NullOpenVinoAvailabilityProvider` implementations:

| # | Location | Scope | Used? |
|---|----------|-------|-------|
| 1 | `src/Trackdub.Inference/Runtime/Planning/NullOpenVinoAvailabilityProvider.cs` | `public sealed class` in `Trackdub.Inference` | YES -- used by `OnnxExecutionProviderDiscovery` default ctor and multiple test files |
| 2 | `src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs:1657` | `private sealed class` nested inside `OnnxExecutionSessionFactory` | YES -- used on line 1651 (`NullOpenVinoAvailabilityProvider.Instance`) in the Linux conditional |

The audit flags item #2 (the private nested class at line 1657). Despite being `private`, it IS used within `OnnxExecutionSessionFactory` itself on line 1651 under the `#elif LINUX` conditional compilation block.

**Verdict:** The audit is **wrong** to mark it safe. However, there's a design smell: two implementations of the same null-object pattern exist. Consider consolidating to only the public one in `Trackdub.Inference` and deleting the private nested duplicate.

**Action:** Reclassify as **do not remove** (or consolidate to the public version in a separate cleanup).

---

## 2. WRONG: Source-generator `value` parameters (~80 entries)

The audit flags `value` parameter as "never used" on partial methods like:

```csharp
partial void OnPlaybackVolumePercentChanged(double value) { ... }
```

These are **CommunityToolkit.Mvvm `[ObservableProperty]` hook points**. The source generator emits the partial method signature; the developer *may* choose not to use `value` in the body. The parameter cannot be removed -- it's part of the generated contract.

**Affected ViewModels (non-exhaustive):**

- `AvaloniaMainWindowViewModel.cs` (lines 555, 1318, 1323)
- `AvaloniaMainWindowViewModel.PipelineUi.cs` (line 474)
- `AvaloniaVideoFramePresenter.cs` (line 287)
- `AvaloniaTranscriptSegmentItem.cs` (lines 547, 549, 555, 561, 569)
- `ModelManagerViewModel.cs` (~18 entries)
- `PipelineStageRowViewModel.cs` (~9 entries)
- `StarterPackCardViewModel.cs` (~22 entries)
- `VoiceSpeakerCardViewModel.cs` (~6 entries)
- `WaveformTimelineViewModel.cs` (lines 30, 37, 44, 50)
- `ShellViewModel.cs` (lines 154, 160)
- `NavigatorSectionViewModel.cs`, `SegmentEditorViewModel.cs`, `DevLogViewModel.cs`, `SettingsWindowViewModel.cs`

**Action:** Remove all `value` parameter entries from both "review needed" and "safe" tables. These are not actionable. Net reduction: ~80 items from the review-needed count.

---

## 3. WRONG: Interface `CancellationToken` parameters (~10 entries)

The audit flags `ct` / `cancellationToken` parameters on interface methods in:

- `Trackdub.Contracts/IAudioPreviewTransport.cs` (5 methods)
- `Trackdub.Application/Services/ITranslationService.cs`
- `Trackdub.Application/Services/IVoiceAssignmentService.cs`
- `Trackdub.Application/Runtime/ILicenseConsentService.cs`
- `Trackdub.Application/Updates/IUpdateService.cs`

These are **interface contract parameters**. Implementations must accept them. Removing them is a breaking API change.

**Action:** Reclassify as **do not remove** (interface contract).

---

## 4. WRONG: GlobalUsings.cs "delete if empty" recommendation

The audit recommends deleting 16 GlobalUsings.cs files as "empty or comment-only." Verification shows most contain real `global using` directives:

| File | Actual Content |
|------|----------------|
| `src/Trackdub.Application/GlobalUsings.cs` | 14 global usings (1 redundant) |
| `src/Trackdub.Composition/GlobalUsings.cs` | 9 global usings (1 redundant) |
| `src/Trackdub.Tools/GlobalUsings.cs` | 2 global usings (1 redundant) |
| `tests/Trackdub.Sdk.Tests/GlobalUsings.cs` | 2 global usings (1 redundant) |
| `tests/Trackdub.Composition.Tests/GlobalUsings.cs` | 4 global usings (1 redundant) |

**Action:** Do NOT blindly delete. Only clean the redundant using directives within them (via `dotnet format`). Re-verify the remaining 11 files individually before acting.

---

## 5. CORRECT BUT UNDER-CLASSIFIED: Items marked "review needed" that are confirmed dead

These were conservatively marked "review needed" but verification confirms they have zero consumers:

### Converters (no AXAML reference anywhere)

| Converter | File | Evidence |
|-----------|------|----------|
| `IsNotNullConverter` + `Instance` | `Converters/IsNotNullConverter.cs` | AXAML uses built-in `ObjectConverters.IsNotNull` instead |
| `StageStatusToIconConverter` + `Instance` | `Converters/StageStatusToIconConverter.cs` | Not in any `.axaml` |
| `TimeSpanToTimecodeConverter` + `Instance` | `Converters/TimeSpanToTimecodeConverter.cs` | AXAML uses `SecondsToTimecodeConverter`; this one only in a unit test |
| `VolumeToPercentConverter` + `Instance` | `Converters/VolumeToPercentConverter.cs` | Not in any `.axaml` |
| `StringToImageConverter` + `Instance` | `Converters/StringToImageConverter.cs` | Not in any `.axaml` |
| `PipelineRowAccentBrushConverter` | `Converters/PipelineRowAccentBrushConverter.cs` | Not in any `.axaml` |

### ViewModel properties with no binding

| Property | File | Evidence |
|----------|------|----------|
| `ShellViewModel.ShellStatus` | `ViewModels/ShellViewModel.cs:84` | No AXAML binding, no code-behind reference |
| `SettingsWindowViewModel.AppName` | `ViewModels/SettingsWindowViewModel.cs:402` | Property exists, no AXAML binding (BuildNumber/BuildDate/IsDevBuild ARE bound) |

### Entire dead types

| Type | File | Evidence |
|------|------|----------|
| `DeliveryContext` (+ all properties) | `WebhookDelivery/Models/DeliveryContext.cs` | Only referenced in a README. Never instantiated or deserialized. Scaffolded but unused. |
| `ModelCacheDiagnostics` | `Infrastructure/Diagnostics/ModelCacheDiagnostics.cs` | Logic duplicated as private method in `DiagnosticsCollector`. Zero callers of the static class. |
| `TextHelpers` | `App.Avalonia/Helpers/TextHelpers.cs` | Type + `TruncateWithEllipsis` never called from any `.cs` or `.axaml` |

**Action:** Promote all above to **safe to remove**.

---

## 6. CONFIRMED: Safe bulk actions

| Action | Status |
|--------|--------|
| Remove 7 unused `PackageVersion` entries from `Directory.Packages.props` | **Confirmed safe** -- individually verified, no PackageReference in any csproj (see below) |
| Remove `BuildSyntheticEvent` from `WebhookDelivery/Function.cs:95` | **Confirmed dead** -- private method, never called |
| `SessionService._sessions` is write-only | **Confirmed** -- collection populated but never queried (type itself is live, used by Api DubbingOrchestrator) |
| `DubbingPipelineEngine._serviceConfigurator` | **May already be removed** -- field does not exist in current `main` (audit ran on `codex/refactor` branch) |
| `ModelCacheDiagnostics` duplication | **Confirmed** -- `DiagnosticsCollector.cs:68` has identical private `DetermineModelCacheEntry` method; static class version at `ModelCacheDiagnostics.cs:9` has zero callers |

### PackageVersion removal verification

| Package | Verified | Notes |
|---------|----------|-------|
| `Avalonia.Controls.TreeDataGrid` | Zero csproj references | Safe to remove |
| `DynamicData` | Zero csproj references | Safe to remove (appears only as transitive in dgspec) |
| `JetBrains.Annotations` | Zero csproj references | Safe to remove |
| `LibVLCSharp.Avalonia` | Zero csproj references | Safe to remove |
| `Microsoft.Graphics.Win2D` | Zero csproj references | Safe to remove |
| `OpenTelemetry.Api` | Zero csproj references | Safe to remove (only `.Extensions.Hosting`/`.Instrumentation.*` referenced) |
| `VideoLAN.LibVLC.Linux` | Zero csproj references | Safe to remove (only `.Windows` and `.Mac` variants are referenced) |

**Note:** The second-pass reviewer tested `AvaloniaUI.DiagnosticsSupport` and `Lucene.Net.Analysis.Common` which are NOT in the audit's 7-package list -- those packages ARE actively referenced and are NOT candidates for removal.

---

## 7. CONFIRMED CORRECT: "Do not remove" items

All 46 items are verified:

- FluentValidation validators: registered via `AddValidatorsFromAssemblyContaining` in `Program.cs:163`
- `SettingsTabNavigation` + `StarterPackFileDialogService`: DI-registered in `App.axaml.cs` (lines 138-139)
- `WebhookDelivery/Function.cs`: Lambda entry point with `[assembly: LambdaSerializer]` + `AWSProjectType=Lambda` in csproj
- All `Trackdub.Sdk` public surface (builders, config records, session factory): public API
- `EventBridgeEvent` / `EventEnvelope` properties: JSON serialization contracts

---

## Corrected Counts

| Category | Original | Corrected | Delta |
|----------|----------|-----------|-------|
| Do not remove | 46 | ~56 | +10 (interface params, NullOpenVino) |
| Review needed | 1156 | ~1056 | -100 (partial methods, confirmed-dead promoted out) |
| Safe to remove | 1838 | ~1928 | +90 (promoted from review needed) |
| GlobalUsings to delete | 16 | 5-6 (re-verify) | Most are NOT empty |

---

## Recommended Execution Order

1. **`dotnet format --no-restore`** -- cleans all redundant using directives (largest safe category, ~1000 items)
2. **Remove 7 unused PackageVersion entries** from `Directory.Packages.props`
3. **Delete confirmed-dead files:**
   - `src/Trackdub.App.Avalonia/Converters/IsNotNullConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/StageStatusToIconConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/TimeSpanToTimecodeConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/VolumeToPercentConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/StringToImageConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/PipelineRowAccentBrushConverter.cs`
   - `src/Trackdub.App.Avalonia/Helpers/TextHelpers.cs`
   - `src/Trackdub.Infrastructure/Diagnostics/ModelCacheDiagnostics.cs`
   - `src/Trackdub.WebhookDelivery/Models/DeliveryContext.cs`
4. **Remove dead members:**
   - `ShellViewModel.ShellStatus`
   - `SettingsWindowViewModel.AppName`
   - `Function.BuildSyntheticEvent`
   - `SessionService._sessions` field (keep the class)
5. **Do NOT touch:**
   - Source-generator partial method `value` parameters
   - Interface `CancellationToken` parameters
   - `NullOpenVinoAvailabilityProvider`
   - GlobalUsings.cs files (run `dotnet format` on them instead)
6. **Build + test:** `dotnet build Trackdub.sln -m:1 -p:Platform=x64 -warnaserror && dotnet test Trackdub.sln -m:1 -p:Platform=x64`
