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
- If the current working directory is **Trackdub core** → **do not attempt a bump.** State that the submodule lives in the gated repo, and point at the canonical process doc:
  - `../Trackdub-gated/.opencode/context/processes/submodule-pin-bump.md`
  - and `../Trackdub-gated/.opencode/navigation.md` for the gated system's context index.
  Then change into that verified checkout and invoke `/pin` there; this core-installed command is the entry-point pointer, not a request to mutate the core checkout.

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

Core:
```bash
dotnet build Trackdub.slnx -m:1
```

Gated:
```bash
dotnet build Trackdub.slnx -m:1
```

Both must succeed. If only one was run, that repo is `NOT VERIFIED`. For a stronger gate, run `/validate` in the core repo afterwards:
```
dotnet restore Trackdub.slnx -m:1
dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
dotnet test Trackdub.slnx --configuration Release --no-build -m:1
```

## Step 5 — breaking-change triage

The new pin may carry breaking changes the gated repo must absorb. For each, check whether the gated repo depends on it and report:

- **Dependency graph changes** — new/removed/renamed projects, changed `ProjectReference` sets. The gated app references core `Application`, `Composition`, `Domain`, `Licensing`, `Media.Playback`, `Sdk`; test projects additionally reference `Contracts`. Confirm all still resolve.
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