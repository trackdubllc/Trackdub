# src/Trackdub.Inference/Runtime

## Purpose

Runtime planning.

## What belongs here

Execution providers, session factories, cache, manifests, downloads.
Milestone 5 planner code lives under `Planning/` and keeps provider policy milestone-scoped.

## What should not go here

WinUI controls.

## Agent guidance

Keep changes scoped to this directory's purpose. If a task requires crossing boundaries, update the relevant architecture note or ADR first.
Commercial-safe ASR remains intentionally blocked with the current manifest inventory until a later milestone introduces a safe ASR baseline.
