# src/Trackdub.Application/Projects

## Purpose

Project use cases.

## What belongs here

Create/open/resume project handlers.

## What should not go here

WinUI dialogs or database SQL.

## Agent guidance

Keep changes scoped to this directory's purpose. If a task requires crossing boundaries, update the relevant architecture note or ADR first.

Stem separation artifacts are grouped by the engine family that produced them under
`artifacts/stems/{stageRunId}/{engineFamily}/`. Latent separator families such as
`hush-dialogue` and `bs-roformer` should get their own folder only if they become
active for a project again; do not create those folders by default.
