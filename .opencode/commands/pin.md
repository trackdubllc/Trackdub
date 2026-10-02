---
description: "Bump the pinned Trackdub core submodule in Trackdub-gated and verify both repos"
agent: subagents/pipeline-inference
---

Bump the pinned Trackdub core submodule and verify both repos still build.

> **Routing note.** This command mutates git state (`git checkout` of the submodule, `git add`, `git commit`) and therefore cannot run on a read-only agent. `pipeline-inference` is the only core subagent with edit authority. Run it there. Do **not** delegate the mutation to `core-diagnostics` or `validation-gate` — both are `edit: deny` and would fail mid-procedure, potentially after the submodule checkout but before the commit, leaving the gated repo in a dirty, half-applied state. If that happens, recover before doing anything else: `git -C "$GATED_ROOT/external/Trackdub" checkout <previous-sha>` then `git -C "$GATED_ROOT" status --short` to confirm the gitlink is back. (`$GATED_ROOT` is resolved in Step 0; resolve it first if you have not.)

**Pin target:** `$1` — the tag or SHA to pin `external/Trackdub` to. If `$1` is empty: **stop.** Show the candidate tags and recent SHAs from the submodule's `origin` and ask which one to pin. **Do not pick a version yourself.**

## Step 0 — resolve the gated root

This command ships in the **core** repo but the submodule it bumps lives in
**Trackdub-gated**. So it must work when invoked from core, which is where it is installed.

Resolve `GATED_ROOT` once and use it for every subsequent command. Every git command below
therefore carries an explicit `-C`, and every dotnet command an absolute solution path, so the
procedure is correct no matter which directory you invoked it from. Do not assume you are in
the gated repo, and do not `cd` — resolve, then address everything.

**Use the quoted forms exactly as written** — `"$GATED_ROOT"` and
`"$GATED_ROOT/external/Trackdub"`. This agent's bash allow-list matches on command text and a
wildcard cannot span a shell variable, so `git -C "$GATED_ROOT" ...` is permitted only in that
spelling. If a step appears blocked, the cause is almost certainly a reformatted path.

```bash
# Already in the gated repo? Use it. Otherwise find a sibling checkout.
GATED_ROOT="$(git rev-parse --show-toplevel)"
[ -f "$GATED_ROOT/.gitmodules" ] || GATED_ROOT="$(git -C ../Trackdub-gated rev-parse --show-toplevel 2>/dev/null)"
[ -f "$GATED_ROOT/.gitmodules" ] || GATED_ROOT=""
```

Verify before proceeding — all three must hold:

```bash
test -n "$GATED_ROOT" && echo "root: $GATED_ROOT"
test -f "$GATED_ROOT/.gitmodules"            && echo "gitmodules: ok"
test -d "$GATED_ROOT/external/Trackdub"      && echo "submodule: ok"
git -C "$GATED_ROOT/external/Trackdub" remote -v
```

If `GATED_ROOT` is empty, no `.gitmodules` was found, or the submodule is missing: report
**NOT AVAILABLE HERE**, name what you searched, and stop. Do not fabricate the procedure and
do not attempt a bump against the wrong repository — that would move a real pin on a guess.
If `GATED_ROOT` resolved to the **core** repo (its root has no `.gitmodules`, so the
`[ -f ]` test is what catches this), say so explicitly: you are in the source repo, and the
consumer is somewhere else.

> The core repo is not the only possible host for this command — a gated checkout carries its
> own copy of a pin-bump procedure at
> `<gated-root>/.opencode/context/processes/submodule-pin-bump.md`. Read it when present: it
> may describe steps this file does not, and it is authoritative for that repo.

## Step 1 — fetch

```bash
git -C "$GATED_ROOT/external/Trackdub" fetch origin
```

## Step 2 — checkout the pin

```bash
git -C "$GATED_ROOT/external/Trackdub" checkout <tag-or-sha>
```

Record the resolved SHA. `external/Trackdub` is a **pinned read-only submodule — never edit files inside it**. Any diff under `external/Trackdub` other than the gitlink pointer is a hard failure; report and stop.

## Step 3 — commit the gitlink

```bash
git -C "$GATED_ROOT" add external/Trackdub
git -C "$GATED_ROOT" commit -m "Bump core pin"
```

**Always `-m`.** The interactive editor is broken in this environment. Use an imperative title (`Bump core pin`). Never `git commit` bare.

## Step 4 — verify BOTH repos

The two builds run in **different directories**. Getting this wrong makes one block silently
build the wrong solution and still print PASS, which is exactly the false-verdict this command
exists to prevent. `dotnet build` takes a solution path, so there is no `cd` needed — address
each by absolute path under `$GATED_ROOT`.

**Core** — the pinned submodule, which *is* the core repo:
```bash
git -C "$GATED_ROOT/external/Trackdub" rev-parse --show-toplevel   # confirm before building
dotnet build "$GATED_ROOT/external/Trackdub/Trackdub.slnx" -m:1
```

**Gated** — the consumer:
```bash
git -C "$GATED_ROOT" rev-parse --show-toplevel   # confirm this is the GATED root, not the submodule
dotnet build "$GATED_ROOT/Trackdub.slnx" -m:1
```

Both must succeed. Verify each root with `rev-parse` before its build, and record both in the report. If only one ran, that repo is `NOT VERIFIED` — do not emit a PASS row for the one you skipped.

For a stronger core gate, run the sequence against the submodule (note the `-C` on the git calls
and the submodule path on the dotnet calls):
```bash
git -C "$GATED_ROOT/external/Trackdub" rev-parse --show-toplevel
dotnet restore "$GATED_ROOT/external/Trackdub/Trackdub.slnx" -m:1
dotnet build "$GATED_ROOT/external/Trackdub/Trackdub.slnx" --configuration Release --no-restore -m:1 -warnaserror
dotnet test "$GATED_ROOT/external/Trackdub/Trackdub.slnx" --configuration Release --no-build -m:1
```

## Step 5 — breaking-change triage

The new pin may carry breaking changes the gated repo must absorb. For each, check whether the gated repo depends on it and report:

All checks below address the **gated** repo, not core — you are auditing what the new pin does
to its consumer.

- **Dependency graph changes** — new/removed/renamed projects, changed `ProjectReference` sets. The gated app references core `Application`, `Composition`, `Domain`, `Licensing`, `Media.Playback`, and `Sdk`; test projects additionally reference `Contracts`. `Trackdub.App.Avalonia` **also** references `Trackdub.Benchmarks` and `Trackdub.DubBench` beyond that list — confirm all eight resolve, and check those two explicitly since `AGENTS.md` does not name them.
  ```bash
  grep -rhoP '(?<=ProjectReference Include=")[^"]+' "$GATED_ROOT"/src/Trackdub.App.Avalonia/*.csproj | tr '\134' '/' | awk -F/ '{print $NF}' | sed 's/\.csproj$//' | sort -u
  ```
  `ProjectReference` paths are relative and backslash-separated on Windows
  (`..\..\external\Trackdub\src\Trackdub.Application\...`), so normalise to the project name
  before comparing. Use the octal `'\134'` rather than `'\\'` — some `tr` builds reject an
  unescaped trailing backslash. Verified output at this pin is exactly eight projects:
  `Trackdub.Application`, `Trackdub.Benchmarks`, `Trackdub.Composition`, `Trackdub.Domain`,
  `Trackdub.DubBench`, `Trackdub.Licensing`, `Trackdub.Media.Playback`, `Trackdub.Sdk`.
- **Desktop stage order** — `PipelineExecutionCoordinator` in the gated app defines its own `stageOrder` from `StageNames.*`, and it deliberately differs from core's `DubbingPipelineStages.ExtendedStageOrder` (it interleaves `Diarization` after `TextRefinementAsr`; core runs `Diarization` before `Asr`). A core change that adds or reorders a stage leaves that array untouched and the stage **silently never runs in the desktop app** while core and CLI stay green. Both compile, so nothing catches it but this check:
  ```bash
  grep -oP '(?<=StageNames\.)[A-Za-z]+' "$GATED_ROOT"/src/Trackdub.App.Avalonia/Services/PipelineExecutionCoordinator.cs | sort
  git -C "$GATED_ROOT/external/Trackdub" show HEAD:src/Trackdub.Domain/StageRuns/StageNames.cs | grep -oP '(?<=const string )[A-Za-z]+' | sort
  ```
  A core constant with no desktop counterpart is `IMPACT`. Do not "fix" it by editing the submodule.
- **Domain and contract type changes** — moved/renamed types, changed records, signature changes on `Application`/`Sdk` entry points.
- **Runtime flavor / EP changes** — `TrackdubOrtRuntimeFlavor` (Ort / Dnnl / WinML), provider selection, native asset layout. Gated packaging depends on this.
- **Model manifest changes** — added/removed models, license field changes, checksum changes. Commercial-only; unknown license is unsafe. The manifest tooling lives in the submodule, so validate from there and report it as a core-side result:
  ```bash
  python3 "$GATED_ROOT/external/Trackdub/tools/ci/validate-manifest-schema.py"
  python3 "$GATED_ROOT/external/Trackdub/tools/ci/verify-manifest-hashes.py"
  ```
- **Banned API / analyzer changes** — new `BannedSymbols.txt` entries or analyzer diagnostics will surface as warnings in the gated build.
- **Build/property changes** — TFM changes, package additions, `packages.lock.json` regen. Lock files are never hand-merged; regenerate with `dotnet restore "$GATED_ROOT/Trackdub.slnx" --force-evaluate -m:1`.
- **Public API surface** — anything the gated desktop shell binds to at compile time.

Mark each `OK` (no impact, with the check that showed it), `IMPACT` (what breaks, where), or `NOT VERIFIED`.

## Step 6 — report

```markdown
# Core pin bump: <old> -> <new>
Resolved SHA: <sha>

## Builds
Both repos have a solution called `Trackdub.slnx`, so the command text alone does not say
which repo was built. Record the root used; a row without one is not attributable.

| Repo | Solution built | Result |
|---|---|---|
| core | `<GATED_ROOT>/external/Trackdub/Trackdub.slnx` | PASS / FAIL / NOT VERIFIED |
| gated | `<GATED_ROOT>/Trackdub.slnx` | PASS / FAIL / NOT VERIFIED |

Substitute the resolved absolute root for `<GATED_ROOT>` in the report. If only one ran, the
other is `NOT VERIFIED` — do not fill the row with the command you did not execute.

## Submodule integrity
- files edited inside external/Trackdub: none (required)

## Breaking-change triage
- <area>: OK | IMPACT (<what>) | NOT VERIFIED (<what would prove it>)

## Commit
<sha> Bump core pin
```

## Rules

- Never hand-edit anything under `external/Trackdub`. If the bump requires a core change, stop and say so — that change belongs in the core repo, in its own PR.
- Never report success on a partial build. One repo built and one not is not a pass.
- Do not merge, push, or open a PR unless explicitly asked. Commit locally and report.
- If a build fails only because of an expected breaking change, report `FAIL` with the exact error and the triage item that predicts it. Do not patch the gated repo to make the number go green.