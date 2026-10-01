---
description: "Full CI-equivalent gate: restore, Release build with warnings-as-errors, tests, plus architecture bounds audit"
agent: subagents/validation-gate
---

Run the Trackdub core CI-equivalent validation gate.

**Scope hint:** `$1` (optional). Examples: `core`, `inference`, `media`, `Application`, a full test project name like `Trackdub.Application.Tests`, or empty for the whole solution. If empty, treat as whole-solution scope.

**State at invocation:**

```
$ git status --short
$ git diff --stat HEAD
```

## Procedure

You are running *as* `validation-gate` (`edit: deny`) — do the work directly rather than delegating. You cannot modify code to make a gate pass, and you must not try: a gate you edited into passing is not a passing gate. Run the following gates **in order**. Do not reorder, do not parallelize, do not skip a gate because a previous one "looks fine".

### Gate 1 — restore
```
dotnet restore Trackdub.slnx -m:1
```

### Gate 2 — Release build, warnings as errors
```
dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
```

### Gate 3 — Release tests, no rebuild
```
dotnet test Trackdub.slnx --configuration Release --no-build -m:1
```

### Gate 4 — architecture bounds audit
Four sub-checks. Report each separately:
1. **Dependency direction** — every edge in the `AGENTS.md` "Dependency Architecture" graph must match the actual `<ProjectReference>` set in `src/**.csproj`. `Trackdub.Domain`, `Trackdub.Licensing`, `Trackdub.Analyzers`, `Trackdub.OnnxRuntime.Dnnl.Native` reference nothing. `Trackdub.Contracts` references only `Trackdub.Domain`. No inference type may leak upward into Domain/Contracts. Cross-check with `.opencode/ARCHITECTURE.md`.
2. **`tests/Trackdub.Architecture.Tests`** — `DependencyGraphTests` (diagram/csproj parity, acyclicity, ADR-0011 Contracts isolation, ONNX/WinML/DNNL asset invariants, portable RID lock graphs), `LicensingIsolationTests`, `StageNameConsistencyTests`, `WorkflowTriggerTests`. These run inside Gate 3; if Gate 3 was skipped, run them explicitly:
   ```
   dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1
   ```
3. **`BannedSymbols.txt`** — `Path.Combine` is a banned API via `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`BannedSymbols.txt` + `Directory.Build.props`). Currently a **warning**, not an error, because ~337 existing files still use it. New or changed code must not add occurrences. Report the count of `Path.Combine` hits in files touched by this change.
4. **`Trackdub.Analyzers`** — confirm analyzer diagnostics are active for the touched projects and that no new diagnostic IDs appear in Gate 2 output. Optional corroboration: `python tools/ci/verify-dependency-graph.py`.

### Gate 5 — `packages.lock.json` integrity
```
git diff --name-only HEAD -- '*packages.lock.json'
git diff --check HEAD -- '*packages.lock.json'
```
- A hand-merged lock file is a hard FAIL. `packages.lock.json` must never be conflict-resolved by hand.
- Lock files must be regenerated, not edited. Any diff must be reproducible by:
  ```
  dotnet restore Trackdub.slnx --force-evaluate -m:1
  ```
- On conflict markers in a lock file, do **not** hand-resolve. Take a side and regenerate:
  ```
  git checkout --ours -- <path>/packages.lock.json
  # or
  git checkout --theirs -- <path>/packages.lock.json
  dotnet restore Trackdub.slnx --force-evaluate -m:1
  ```
- Portable RID restore graphs must remain intact: `net10.0/{win-x64,win-arm64,linux-x64,linux-arm64,osx-x64,osx-arm64}` in `Trackdub.Contracts`, `Trackdub.Domain`, `Trackdub.Inference`; plus the `net10.0-windows10.0.19041` graphs for `Trackdub.Inference.Onnx`.

## Scoping rules

- **Whole-solution scope (default):** run Gates 1–5.
- **User explicitly scoped to a single test project** (e.g. `/validate Trackdub.Application.Tests`): Gates 1–3 may be narrowed to that project — but you MUST state in the output, verbatim, that the solution-wide Release gate was skipped and why. Gates 4 and 5 still run at solution scope.

## Reporting

Emit a table:

| # | Gate | Command | Result | Evidence |
|---|------|---------|--------|----------|
| 1 | restore | `dotnet restore Trackdub.slnx -m:1` | PASS / FAIL / NOT VERIFIED | exit code |
| 2 | Release build -warnaserror | ... | ... | warning/error count |
| 3 | Release tests | ... | ... | passed/failed/skipped counts |
| 4a | dependency direction | `tools/ci/verify-dependency-graph.py` | ... | offending edges |
| 4b | Architecture.Tests | ... | ... | failing test names |
| 4c | BannedSymbols | ... | ... | new `Path.Combine` count |
| 4d | Analyzers | ... | ... | new diagnostic IDs |
| 5 | packages.lock.json integrity | `git diff --name-only HEAD -- '*packages.lock.json'` | ... | conflicting paths |

**Final verdict line:** `VALIDATION: PASS` or `VALIDATION: FAIL (first failing gate: <N>)`.

## Rules

- On **any** failure: **stop**. Report the first failing gate with the exact command and a verbatim output excerpt (enough lines to locate the error, no paraphrase). Never summarize a failure as success, and never continue past a failure to produce a PASS table.
- **Do not claim completion on partial evidence.** Anything not actually executed is reported as `NOT VERIFIED`, never as PASS, and never as "expected to pass".
- Never estimate, extrapolate, or infer a gate result from a previous run's cache, a green CI badge, or a comment.
- If a gate cannot run (missing SDK, missing workload, missing model fixture, timeout), that gate is `NOT VERIFIED` with the blocking reason and the command needed to unblock.
- Do not modify source to make a gate pass. You have `edit: deny` and you should not attempt to work around it. Report; the user decides.