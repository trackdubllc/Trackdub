---
description: "Bump the pinned Trackdub core submodule in Trackdub-gated and verify both repos"
agent: subagents/pipeline-inference
---

Bump the pinned Trackdub core submodule and verify both repos still build.

> **Routing note.** This command mutates git state (`git checkout` of the submodule, `git add`, `git commit`) and therefore cannot run on a read-only agent. `pipeline-inference` is the only core subagent with edit authority. Run it there. Do **not** delegate the mutation to `core-diagnostics` or `validation-gate` — both are `edit: deny` and would fail mid-procedure, potentially after the submodule checkout but before the commit, leaving the gated repo in a dirty, half-applied state. If that happens, recover before doing anything else: `git -C external/Trackdub checkout <previous-sha>` then `git status` to confirm the gitlink is back.

**Pin target:** `$1` — the tag or SHA to pin `external/Trackdub` to. If `$1` is empty: **stop.** Show the candidate tags and recent SHAs from the submodule's `origin` and ask which one to pin. **Do not pick a version yourself.**

## Step 0 — determine where you are

This command lives in the core repo's `.opencode`, but the submodule it bumps lives in **Trackdub-gated**.

- If the current working directory is **Trackdub-gated** (`.gitmodules` present, `external/Trackdub` present) → run the procedure below.
- If the current working directory is **Trackdub core** → **do not attempt a bump.** State that the submodule lives in the gated repo, and point at the canonical process doc, resolved from a sibling checkout of that repo:
  - `<gated-root>/.opencode/context/processes/submodule-pin-bump.md`
  - and `<gated-root>/.opencode/navigation.md` for the gated system's context index.

  A relative `../Trackdub-gated/...` path only resolves if the sibling happens to be laid out that way, which is a property of one machine, not of the repos. Locate the gated root first:
  ```bash
  git -C ../Trackdub-gated rev-parse --show-toplevel   # adjust the path until this succeeds
  ```
  If no gated checkout can be found, report the pin procedure as **NOT AVAILABLE HERE** and stop. Do not fabricate the procedure from this file — the gated system owns it.

Detect this, do not assume. `git rev-parse --show-toplevel` and the presence of `.gitmodules` tell you.

## Step 1 — fetch

```bash
git -C external/Trackdub fetch origin
```

## Step 2 — checkout the pin

```bash
git -C external/Trackdub checkout <tag-or-sha>
```

Record the resolved SHA. `external/Trackdub` is a **pinned read-only submodule — never edit files inside it**. Any diff under `external/Trackdub` other than the gitlink pointer is a hard failure; report and stop.

## Step 3 — commit the gitlink

```bash
git add external/Trackdub
git commit -m "Bump core pin"
```

**Always `-m`.** The interactive editor is broken in this environment. Use an imperative title (`Bump core pin`). Never `git commit` bare.

## Step 4 — verify BOTH repos

The two builds run from **different working directories**. Getting this wrong makes one block silently build the wrong solution and still print PASS, which is exactly the false-verdict this command exists to prevent. Run each from the directory named in its header.

**Core** — from inside the pinned submodule (`external/Trackdub`), which *is* the core repo:
```bash
git -C external/Trackdub rev-parse --show-toplevel   # confirm before building
dotnet build external/Trackdub/Trackdub.slnx -m:1
```

**Gated** — from the gated repo root, the directory you are already in at Step 0:
```bash
git rev-parse --show-toplevel                        # confirm this is the GATED root, not the submodule
dotnet build Trackdub.slnx -m:1
```

Both must succeed. Verify each root with `rev-parse` before its build, and record both in the report. If only one ran, that repo is `NOT VERIFIED` — do not emit a PASS row for the one you skipped.

For a stronger core gate, run `/validate` inside the submodule afterwards (note the `-C`, since your cwd is the gated root):
```
git -C external/Trackdub rev-parse --show-toplevel
dotnet restore external/Trackdub/Trackdub.slnx -m:1
dotnet build external/Trackdub/Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
dotnet test external/Trackdub/Trackdub.slnx --configuration Release --no-build -m:1
```

## Step 5 — breaking-change triage

The new pin may carry breaking changes the gated repo must absorb. For each, check whether the gated repo depends on it and report:

- **Dependency graph changes** — new/removed/renamed projects, changed `ProjectReference` sets. The gated app references core `Application`, `Composition`, `Domain`, `Licensing`, `Media.Playback`, and `Sdk`; test projects additionally reference `Contracts`. `Trackdub.App.Avalonia` **also** references `Trackdub.Benchmarks` and `Trackdub.DubBench` beyond that list — confirm all eight resolve, and check those two explicitly since `AGENTS.md` does not name them. Verify with `grep -oP '(?<=ProjectReference Include=")[^"]+' src/Trackdub.App.Avalonia/*.csproj`.
- **Domain and contract type changes** — moved/renamed types, changed records, signature changes on `Application`/`Sdk` entry points.
- **Runtime flavor / EP changes** — `TrackdubOrtRuntimeFlavor` (Ort / Dnnl / WinML), provider selection, native asset layout. Gated packaging depends on this.
- **Model manifest changes** — added/removed models, license field changes, checksum changes. Commercial-only; unknown license is unsafe. Manifest schema changes must pass `tools/ci/validate-manifest-schema.py` and `tools/ci/verify-manifest-hashes.py`.
- **Banned API / analyzer changes** — new `BannedSymbols.txt` entries or analyzer diagnostics will surface as warnings in the gated build.
- **Build/property changes** — TFM changes, package additions, `packages.lock.json` regen. Lock files are never hand-merged; regenerate with `dotnet restore Trackdub.slnx --force-evaluate -m:1`.
- **Public API surface** — anything the gated desktop shell binds to at compile time.

Mark each `OK` (no impact, with the check that showed it), `IMPACT` (what breaks, where), or `NOT VERIFIED`.

## Step 6 — report

```markdown
# Core pin bump: <old> -> <new>
Resolved SHA: <sha>

## Builds
| Repo | Command | Result |
|---|---|---|
| core | `dotnet build Trackdub.slnx -m:1` | PASS / FAIL / NOT VERIFIED |
| gated | `dotnet build Trackdub.slnx -m:1` | PASS / FAIL / NOT VERIFIED |

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