# src/Trackdub.Infrastructure/Logging

## Purpose

Logging setup.

The local app log is written to:

```text
%LOCALAPPDATA%\Trackdub\trackdub.log
```

Check this file first when troubleshooting startup, runtime, model, media, or UI failures.

## What belongs here

Structured logging, log sinks, diagnostic traces.

## What should not go here

Business logic.

## Agent guidance

Keep changes scoped to this directory's purpose. If a task requires crossing boundaries, update the relevant architecture note or ADR first.
