# src/Trackdub.Tools

## Purpose

Developer tools.

## What belongs here

Manifest builders, artifact inspectors, DB tools.

## ModelLab

`model-lab` is a developer-only model optimization lane. It shells out to the
ORT GenAI builder, Olive `auto-opt`, and `Trackdub.Benchmarks`, writes candidate
outputs under `models/<model-root>/<candidate>`, then writes a manifest fragment
under `models/manifest-fragments/`.

The application consumes those generated files through the normal bundled-model
manifest registry. User-facing apps must not run Olive or Python model conversion
as part of startup or pipeline execution.

```powershell
dotnet run --project src/Trackdub.Tools -- model-lab --help
```

## What should not go here

User-facing application code.

## Agent guidance

Keep changes scoped to this directory's purpose. If a task requires crossing boundaries, update the relevant architecture note or ADR first.
