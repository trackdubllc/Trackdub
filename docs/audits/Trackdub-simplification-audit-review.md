# Simplification Audit Review: Accuracy & False Positives

**Source:** `Trackdub-simplification-audit-codex-refactor.md` (ReSharper `jb inspectcode` on `codex/refactor`)  
**Reviewed by:** Kiro, verified against `main` source code (July 2026)

---

## Executive Summary

The audit is structurally sound -- rules, counts, and representative samples are real ReSharper findings. However, several categories have significant false-positive rates or produce dangerous recommendations when applied mechanically:

- **55 items invalid** (`ReplaceWithFieldKeyword` -- wrong LangVersion for most projects)
- **~15 items dangerous** (`UseNameOfInsteadOfToString` -- breaks enum persistence)
- **~30 items impractical** (`AsyncVoidEventHandlerMethod` -- XAML event handler requirement)
- **~30 items test false positives** (`AccessToDisposedClosure` -- safe within test scope)
- **~25 items wrong for config classes** (`MemberCanBePrivate` on IOptions properties)

**Estimated accuracy:** ~95% of items are real findings, but ~225 of 4298 should not be applied as-is.

---

## 1. DANGEROUS: `UseNameOfInsteadOfToString` (21 items)

The audit suggests replacing `tier.ToString()` and `JobStatus.Running.ToString()` with `nameof`. This is **wrong and dangerous**.

**Why it breaks:**

```csharp
// CURRENT (correct):
TenantTier tier = TenantTier.Free;
await _publisher.PublishSubscriptionUpdatedAsync(tenantId, tier.ToString(), ...);
// Produces: "Free"

// AUDIT SUGGESTION (broken):
await _publisher.PublishSubscriptionUpdatedAsync(tenantId, nameof(tier), ...);
// Produces: "tier" (the variable name, NOT the enum value)
```

**Specific dangerous instances:**
- `BillingService.cs:76` -- `TenantTier.Free.ToString()` used in subscription event publishing
- `BillingService.cs:227` -- same pattern
- `BillingService.cs:296` -- same pattern
- `ConcurrencyGuard.cs:60` -- `JobStatus.Running.ToString()` used in DynamoDB filter expressions (persisted query value)
- `DynamoDbJobQueue.cs:68` -- `JobStatus.Queued.ToString()` in DynamoDB expressions

For the DynamoDB cases, even `nameof(JobStatus.Running)` (which does produce `"Running"`) is fragile: renaming the enum member silently changes the query string and breaks against existing persisted data.

**Verdict:** The entire bucket needs manual review. Only cases where code does something like `throw new ArgumentException(nameof(param))` or logging are safe candidates. At minimum **5 of the 5 samples shown are false positives**.

**Action:** Do not bulk-apply. Review each instance individually. The enum-variable and DynamoDB cases must stay as `.ToString()`.

---

## 2. INVALID: `ReplaceWithFieldKeyword` (55 items)

The C# `field` keyword for semi-auto properties requires `LangVersion=preview`.

**Actual LangVersion configuration:**
- `Directory.Build.props`: `LangVersion=latest` (solution-wide default)
- Only 5 projects override to `preview`: `WebhookDelivery`, `Api.Billing.Tests`, `Api.Tests`, `Sdk.Tests`, `Worker.Tests`

The 55 flagged items are overwhelmingly in `App.Avalonia` and `Application` which both inherit `latest`. These suggestions **will not compile**.

**Verdict:** Remove all `ReplaceWithFieldKeyword` items for projects using `LangVersion=latest`. Only valid for the 5 projects explicitly on `preview` (and those have approximately zero instances in this list).

**Action:** Drop entire category from the actionable list unless LangVersion is upgraded solution-wide to `preview`.

---

## 3. IMPRACTICAL: `AsyncVoidEventHandlerMethod` (36 items)

Most flagged methods are XAML event handlers that Avalonia **requires** to be `async void`:

```csharp
// Avalonia XAML: <Button Click="NewFromMediaButton_Click" />
// Handler MUST be async void -- cannot return Task
private async void NewFromMediaButton_Click(object? sender, RoutedEventArgs e) { ... }
```

**Confirmed XAML-bound handlers:**
- `CenterPanelView.axaml.cs:217` -- `NewFromMediaButton_Click`
- `CenterPanelView.axaml.cs:221` -- `OpenProjectButton_Click`
- `GlossaryPanelView.axaml.cs:15` -- event handler
- `CrashReportWindow.axaml.cs:121` -- button handler

Avalonia's event system uses standard .NET event delegates (`EventHandler<RoutedEventArgs>`) which return `void`. You cannot change the return type to `Task` without breaking the event subscription.

**Verdict:** ~30 of 36 are false positives for Avalonia XAML event handlers. Only code-subscribed events (e.g., `observable.Subscribe(async () => ...)`) or manually wired delegates might be fixable.

**Action:** Investigate only non-XAML instances. For XAML handlers, the correct mitigation is wrapping the body in try/catch (which most already do), not changing the signature.

---

## 4. FALSE POSITIVES: `AccessToDisposedClosure` (41 items, mostly tests)

In test code, this pattern is safe:

```csharp
[Fact]
public async Task Some_test()
{
    using var sut = new SystemUnderTest();
    var result = await sut.DoSomethingAsync(); // flagged: "sut disposed in outer scope"
    Assert.True(result);
} // sut disposed here, AFTER all assertions
```

ReSharper flags it because a lambda/async continuation *could* outlive the `using`, but in linear xUnit test methods this never happens.

**Confirmed test false positives:**
- `ConcurrencyGuardTests.cs:180`
- `TaskLauncherDuplicateDetectionPropertyTests.cs:81, 88, 179, 186`

**Verdict:** ~30 of 41 are test code false positives. Any production code instances (e.g., closures passed to background tasks) should be investigated individually.

**Action:** Ignore test instances. Review the ~11 production code instances case-by-case.

---

## 5. PARTIALLY WRONG: `MemberCanBePrivate.Global` (519 items) + `AutoPropertyCanBeMadeGetOnly.Global` (160 items)

These flag properties on **IOptions<T> configuration classes** which ASP.NET binds from `appsettings.json`:

```csharp
// CognitoOptions.cs -- bound via builder.Services.Configure<CognitoOptions>(config)
public sealed class CognitoOptions
{
    public string Region { get; init; } = "";      // flagged: "can be private"
    public string UserPoolId { get; init; } = "";  // flagged: "can be private"
    public string ClientId { get; init; } = "";    // flagged: "can be private"
}
```

Making these `private` breaks configuration binding. The properties use `init` (so `AutoPropertyCanBeMadeGetOnly` is technically already satisfied), but `MemberCanBePrivate` is wrong.

**Affected classes (non-exhaustive):**
- `CognitoOptions` (3 properties)
- `MigrationOptions`
- `WebhookOptions`
- `TaskLauncherOptions` (3 properties)
- `ApiKeyAuthenticationOptions`

**Verdict:** Subtract ~20-30 items from `MemberCanBePrivate.Global` for IOptions/config classes. These must remain `public` (or at minimum `internal`) for config binding to work.

**Action:** Skip all `MemberCanBePrivate` findings on classes that implement options patterns or are registered with `Configure<T>()`.

---

## 6. LOW VALUE: `ForCanBeConvertedToForeach` (12 items)

Spot-checked `UiHelpers.cs` (4 of 12). All loops access elements via `list[i]` on `IReadOnlyList<T>`:

```csharp
for (int i = 0; i < segments.Count; i++)
{
    var seg = segments[i];  // index access
    if (seg.StartSeconds <= position) ...
}
```

While technically convertible (IReadOnlyList implements IEnumerable), index-based access:
- Avoids enumerator allocation (relevant in hot UI render paths)
- Is idiomatic for ordered boundary searches with early break

**Verdict:** Not wrong, but low-value refactoring with potential micro-perf regression in UI code. The existing style is intentional.

**Action:** Low priority. Skip for hot-path UI code.

---

## 7. VALID BUT NEEDS JUDGMENT: `ConvertToPrimaryConstructor` (79 items)

Legitimate for simple DI constructors (field assignment only). The `DynamoDbSubscriptionStore` example is a clean candidate. However, classes with:
- Constructor body logic (validation, initialization)
- Multiple constructors
- `this(...)` chaining

...cannot be cleanly converted.

**Verdict:** Valid for ~60 of 79 (simple DI constructors). Remaining ~19 need manual review.

**Action:** Apply in bulk for obvious DI constructors. Skip classes with constructor body logic.

---

## CONFIRMED CORRECT: Safe to apply mechanically

| Rule | Count | Confidence | Notes |
|------|-------|------------|-------|
| `RedundantSuppressNullableWarningExpression` | 82 | Very high | Remove redundant `!` -- safe, no behavior change |
| `ConditionalAccessQualifierIsNonNullableAccordingToAPIContract` | 17 | Very high | Remove unnecessary `?.` |
| `NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract` | 19 | Very high | Remove unnecessary `??` |
| `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | 31 | High | Simplify dead branches |
| `UseAwaitUsing` | 64 | Very high | `await using` for IAsyncDisposable |
| `RedundantCast` | 31 | Very high | Remove unnecessary casts |
| `RedundantNameQualifier` | 282 | Very high | `dotnet format` handles this |
| `RedundantExplicitArrayCreation` | 17 | Very high | Use `[]` or collection expression |
| `RedundantLambdaParameterType` | 40 | Very high | Remove explicit lambda types |
| `RedundantArgumentDefaultValue` | 42 | High | Remove args matching defaults |
| `RedundantAttributeUsageProperty` | 12 | Very high | Remove redundant attribute props |
| `RedundantSwitchExpressionArms` | 11 | Very high | Remove unreachable arms |
| `RedundantTypeArgumentsOfMethod` | 27 | Very high | Remove inferable type args |
| `MergeIntoPattern` | 202 | High | Style improvement |
| `UseCollectionExpression` | 43 | High | Valid for net10.0 |
| `ChangeFieldTypeToSystemThreadingLock` | 23 | High | Valid for net10.0 (>= net9.0) |
| `PossibleMultipleEnumeration` | 10 | Very high | Real perf issue |
| `InconsistentNaming` | 1604 | High | `dotnet format` + `.editorconfig` |
| `FieldCanBeMadeReadOnly.Local` | 15 | Very high | Safe mechanical fix |
| `SimplifyLinqExpressionUseAll` | 18 | High | Readability improvement |
| `UseObjectOrCollectionInitializer` | 29 | High | Style improvement |
| `ArrangeObjectCreationWhenTypeEvident` | 46 | High | Target-typed `new()` |
| `ConvertClosureToMethodGroup` | 16 | High | Style improvement |
| `RedundantAnonymousTypePropertyName` | 8 | Very high | Safe removal |
| `RedundantAssignment` | 29 | High | Remove dead assignments |
| `VariableCanBeNotNullable` | 23 | High | Tighten nullability |
| `ReturnTypeCanBeNotNullable` | 15 | High | Tighten nullability |
| `CanSimplifyDictionaryTryGetValueWithGetValueOrDefault` | 17 | High | Valid simplification |
| `PropertyCanBeMadeInitOnly.Global` | 91 | Medium | Valid but check IOptions classes |
| `PropertyCanBeMadeInitOnly.Local` | 15 | High | Safe for local/test types |
| `AutoPropertyCanBeMadeGetOnly.Local` | 20 | High | Safe for local types |
| `ParameterHidesMember` | 15 | High | Valid rename candidates |

---

## VALID BUT CASE-BY-CASE

| Rule | Count | Notes |
|------|-------|-------|
| `MethodHasAsyncOverload` | 97 | Valid but verify: some sync calls are intentional (thread-safety, hot paths, sync-over-async avoidance) |
| `AsyncMethodWithoutAwait` | 10 | Valid -- but some may return `Task.FromResult` intentionally for interface compliance |
| `ConvertToAutoProperty` | 15 | Check for side effects in getter/setter before converting |
| `MethodSupportsCancellation` | 14 | Valid but adding CT propagation can be a larger refactor |
| `ParameterOnlyUsedForPreconditionCheck.Local` | 41 | Often intentional guard clauses -- not actually dead parameters |
| `UsingStatementResourceInitialization` | 11 | Valid but verify exception safety of the specific pattern |

---

## Corrected Priority Counts

| Priority | Original | False Positives | Net Actionable |
|----------|----------|----------------|----------------|
| High | 538 | ~100 | ~438 |
| Medium | 1004 | ~75 | ~929 |
| Low | 2756 | ~50 | ~2706 |
| **Total** | **4298** | **~225** | **~4073** |

---

## Recommended Execution Order

### Wave 1: Mechanical (no judgment needed)

```powershell
# Redundant qualifiers, naming, format
dotnet format --no-restore
```

Then apply via ReSharper/Rider cleanup profile:
- `RedundantSuppressNullableWarningExpression` (82)
- `RedundantCast` (31)
- `RedundantExplicitArrayCreation` (17)
- `RedundantLambdaParameterType` (40)
- `RedundantArgumentDefaultValue` (42)
- `RedundantTypeArgumentsOfMethod` (27)
- `RedundantSwitchExpressionArms` (11)
- `RedundantAnonymousTypePropertyName` (8)
- `FieldCanBeMadeReadOnly.Local` (15)

### Wave 2: High-value, safe with minimal judgment

- `UseAwaitUsing` (64)
- `ChangeFieldTypeToSystemThreadingLock` (23)
- `PossibleMultipleEnumeration` (10)
- `ConditionalAccessQualifierIsNonNullableAccordingToAPIContract` (17)
- `NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract` (19)
- `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` (31)
- `UseCollectionExpression` (43)
- `MergeIntoPattern` (202)
- `SimplifyLinqExpressionUseAll` (18)

### Wave 3: Requires per-instance judgment

- `ConvertToPrimaryConstructor` (79) -- skip classes with constructor body logic
- `MethodHasAsyncOverload` (97) -- verify each callsite
- `MemberCanBePrivate.Global` (519) -- skip IOptions classes
- `AutoPropertyCanBeMadeGetOnly.Global` (160) -- skip config/serialization classes
- `PropertyCanBeMadeInitOnly.Global` (91) -- skip IOptions classes

### DO NOT APPLY

- `ReplaceWithFieldKeyword` (55) -- wrong LangVersion
- `UseNameOfInsteadOfToString` (21) -- dangerous for enums, review individually
- `AsyncVoidEventHandlerMethod` (36) -- mostly XAML handlers, cannot change
- `AccessToDisposedClosure` (41) -- mostly test false positives

### After each wave

```powershell
dotnet build Trackdub.sln -m:1 -p:Platform=x64 -warnaserror
dotnet test Trackdub.sln -m:1 -p:Platform=x64
```
