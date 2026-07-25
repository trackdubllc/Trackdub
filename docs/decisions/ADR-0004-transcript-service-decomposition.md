# ADR-0004: Decompose TranscriptProjectService into bounded workflow services

- Status: Accepted
- Date: 2026-04-24

## Context

`src/Trackdub.Application/Transcripts/TranscriptProjectService.cs` was the
single workspace service the WinUI shell talked to for project lifecycle,
transcript workflows, translation workflows, language persistence, and
artifact provenance. The M6 and M7 completion notes described it that way on
purpose: one service kept the vertical slice small.

Milestone 7 is now complete and the next milestones will each add more
cross-cutting responsibilities to the same service:

- **M8 (playback and settings):** hybrid playback seam, segment editor,
  project management.
- **M10 (diarization):** speaker segmentation must update transcript
  revisions and downstream translation invalidation.
- **M11 (TTS):** per-segment voice assignments, take lifecycle, and more
  revision state to track.
- **M14 (mix):** mix plans and ducking state bound to segments and takes.
- **M15 (export):** export provenance and commercial-safety gating.

Each of these milestones would naturally extend `TranscriptProjectService`
unless we draw boundaries now. Continuing to grow a single workspace service
makes it harder to test with focused fakes, harder to reason about concurrency
and invalidation, and harder to hold to the repo's "bounded agent tasks" rule
from AGENTS.md.

## Decision

Decompose `TranscriptProjectService` into bounded workflow services. The target
shape is:

- `ProjectSessionService` — create / open / close a project, manifest
  persistence, transcript-language setting, top-level session state. Owns the
  `.trackdub` project lifecycle and nothing stage-specific.
- `TranscriptWorkflowService` — VAD and ASR orchestration, transcript
  revision management, manual segment editing, transcript-level artifact
  provenance.
- `TranslationWorkflowService` — translation generation from the current
  transcript revision, manual translation-save flow, translation revision
  management, `needs refresh` derivation, translation-level artifact
  provenance.
- `ProjectStateCoordinator` — mediates cross-cutting state, specifically
  "transcript revision changed → translation is now refresh-needed", and any
  analogous invalidation edges M8–M15 add (segment edits, diarization changes,
  TTS takes, mix plans).

Each service is testable against its own fakes. During transition,
`TranscriptProjectService` may remain as a thin façade that composes these
services so the shell does not break in a single commit.

## Consequences

Positive:

- Boundaries match the milestone roadmap: each upcoming stage (diarization,
  TTS, mix, export) has an obvious home that is not "the single workspace
  service."
- Each service is independently testable with its own fakes, which aligns
  with the "Application tests use fakes" guidance in AGENTS.md.
- Invalidation rules live in one mediator instead of being scattered across
  ad hoc checks inside a large service.
- Keeps individual agent tasks bounded (one workflow, one service) as AGENTS.md
  recommends.

Negative:

- More files to navigate and more DI registrations.
- The transition must be staged so the shell (`Trackdub.App.Avalonia`) keeps working
  through the split; the facade step adds temporary indirection.
- Cross-cutting coordination logic that used to be implicit inside one service
  now needs an explicit contract on `ProjectStateCoordinator`.

## Alternatives considered

### Keep `TranscriptProjectService` as the single workspace service

Rejected because every upcoming milestone (M8, M10, M11, M14, M15) adds more
state and more invalidation edges. Not decomposing now pushes all of that into
one class and makes it the main bottleneck for focused fakes, concurrency
reasoning, and PR review scope.

### Split later, when the service actually hurts

Rejected because the M6–M7 shape already mixes lifecycle, transcript, and
translation concerns. Deferring the split until after M10 / M11 means the
refactor has to be done alongside speaker, take, and mix work, which is
exactly when bounded services are most valuable.

### Extract only a state coordinator, leave everything else in one service

Rejected because the coordinator alone doesn't address the growth of workflow
code inside the service. The workflow services are the ones that need their
own fakes and their own tests; pulling out only the mediator leaves the main
service still owning everything.

## References

- `src/Trackdub.Application/Transcripts/`
- `docs/archive/milestones/completions/MILESTONE-6-COMPLETION.md`
- `docs/archive/milestones/completions/MILESTONE-7-COMPLETION.md`
- `docs/archive/roadmaps/MILESTONE-legacy-2026-04.md`
- `MILESTONE.md`
- AGENTS.md — "Prefer bounded agent tasks" guidance

