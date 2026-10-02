---
description: "Full CI-equivalent gate: restore, Release build with warnings-as-errors, tests, plus architecture bounds audit"
agent: subagents/validation-gate
---

Run the Trackdub core validation gate — **stricter than CI, not identical to it.**

CI (`.github/workflows/ci.yml`) builds Release **without** `-warnaserror`. This gate adds it. So this gate can fail on a warning that CI would accept; passing this gate implies CI passes, but failing it does not necessarily mean CI is red. Report it as the stricter gate and say so rather than calling a `-warnaserror` failure a CI failure.

**Scope hint:** `$1` (optional). Examples: `core`, `inference`, `media`, `Application`, a full test project name like `Trackdub.Application.Tests`, or empty for the whole solution. If empty, treat as whole-solution scope.

**Base ref:** `$2` (optional). For a PR, resolve the target base ref from PR metadata when available; otherwise use the supplied ref. Verify it resolves to a commit. If no base ref can be resolved, report committed-branch diff checks as `NOT VERIFIED`; never substitute `HEAD`.
Set `BASE_REF` to that resolved or supplied ref before running the commands below.

**State at invocation:**

```
$ git status --short
$ git diff --stat HEAD
```

When `BASE_REF` is available, verify it with `git rev-parse --verify "$BASE_REF^{commit}"` and inspect `git diff --stat "$BASE_REF"...HEAD`. Changed paths are the union of `git diff --name-only "$BASE_REF"...HEAD` and `git diff --name-only HEAD`. The first includes commits already on the branch; the second includes staged and unstaged worktree changes.

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
3. **`BannedSymbols.txt`** — `Path.Combine` is a banned API via `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`BannedSymbols.txt` + `Directory.Build.props`). Currently a **warning**, not an error, so the build will not catch it. Count only **added** lines across both diffs — committed *and* uncommitted:
   ```bash
   BASE_REF=origin/main   # or the PR base ref
   git diff -U0 "$BASE_REF"...HEAD -- '*.cs' | grep -E '^\+' | grep -v '^+++' | grep -c 'Path\.Combine'   # committed
   git diff -U0 HEAD -- '*.cs'             | grep -E '^\+' | grep -v '^+++' | grep -c 'Path\.Combine'   # uncommitted
   ```
   Any count above 0 is a finding; enumerate by dropping the trailing `-c`. Checking only `git diff HEAD` misses everything already committed on the branch, which is the common case on a PR.
4. **`Trackdub.Analyzers`** — confirm analyzer diagnostics are active for the touched projects and that no new diagnostic IDs appear in Gate 2 output.

### Gate 5 — `packages.lock.json` integrity
```
git diff --name-only "$BASE_REF"...HEAD -- '*packages.lock.json'
git diff --name-only HEAD -- '*packages.lock.json'
git diff --check "$BASE_REF"...HEAD -- '*packages.lock.json'
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

### Gate 6 — required CI checks outside the build and test jobs

Run every check below, regardless of the changed files:

1. `dotnet format Trackdub.slnx --verify-no-changes`; with a base ref, use `--include` for the union of committed and worktree changed `*.cs` paths. Without a base ref, format the full solution and report committed-branch checks as `NOT VERIFIED`.
2. `python3 scripts/ci/check-repository-boundary.py`
3. `python3 scripts/ci/check-audit-mirrors.py`
4. `dotnet build src/Trackdub.Benchmarks.DevHost -c Release --no-restore -f net10.0 -m:1`, then `python3 scripts/ci/check_controlled_matrix_cpu_budget.py`

The Windows, Linux, and macOS CI matrix is not proven by a single-host run. Report any platform without CI evidence as `NOT VERIFIED`.

## Scoping rules

- **Whole-solution scope (default):** run Gates 1–6.
- **User explicitly scoped to a single test project** (e.g. `/validate Trackdub.Application.Tests`): Gates 1–3 may be narrowed to that project — but you MUST state in the output, verbatim, that the solution-wide Release gate was skipped and why. Gates 4–6 still run at solution scope.

## Reporting

Emit a table:

| # | Gate | Command | Result | Evidence |
|---|------|---------|--------|----------|
| 1 | restore | `dotnet restore Trackdub.slnx -m:1` | PASS / FAIL / NOT VERIFIED | exit code |
| 2 | Release build -warnaserror | ... | ... | warning/error count |
| 3 | Release tests | ... | ... | passed/failed/skipped counts |
| 4a | dependency direction | `DependencyGraphTests` in `tests/Trackdub.Architecture.Tests` | ... | offending edges |
| 4b | Architecture.Tests | ... | ... | failing test names |
| 4c | BannedSymbols | ... | ... | new `Path.Combine` count |
| 4d | Analyzers | ... | ... | new diagnostic IDs |
| 5 | packages.lock.json integrity | committed and worktree diff commands above | ... | conflicting paths |
| 6a | Format | `dotnet format Trackdub.slnx --verify-no-changes` | ... | changed C# paths |
| 6b | Repository boundary | `python3 scripts/ci/check-repository-boundary.py` | ... | output |
| 6c | Audit mirrors | `python3 scripts/ci/check-audit-mirrors.py` | ... | output |
| 6d | Controlled-matrix CPU budget | build Benchmarks.DevHost; run `python3 scripts/ci/check_controlled_matrix_cpu_budget.py` | ... | output |
| 7 | CI platform matrix | Windows, Linux, macOS jobs | ... | CI run or NOT VERIFIED |

**Final verdict line:** `VALIDATION: PASS`, `VALIDATION: FAIL (first failing gate: <N>)`, or `VALIDATION: NOT VERIFIED (gates: <N...>)`.

## Rules

- On **any** failure: **stop**. Report the first failing gate with the exact command and a verbatim output excerpt (enough lines to locate the error, no paraphrase). Never summarize a failure as success, and never continue past a failure to produce a PASS table.
- **Do not claim completion on partial evidence.** Anything not actually executed is reported as `NOT VERIFIED`, never as PASS, and never as "expected to pass".
- Never estimate, extrapolate, or infer a gate result from a previous run's cache, a green CI badge, or a comment.
- If a gate cannot run (missing SDK, missing workload, missing model fixture, timeout), that gate is `NOT VERIFIED` with the blocking reason and the command needed to unblock.
- Do not modify source to make a gate pass. You have `edit: deny` and you should not attempt to work around it. Report; the user decides.