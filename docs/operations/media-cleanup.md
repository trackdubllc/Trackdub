# Media cleanup agent workflow

The repository-local [trackdub-media-cleanup skill](../../.claude/skills/trackdub-media-cleanup/SKILL.md) defines how agents diagnose media quality, preserve originals, inspect real readiness, use existing Trackdub project workflows, and verify before delivery. Load that file directly when your agent does not automatically discover `.claude/skills/`.

This is a documentation/skill adaptation, **not a new cleanup command or runtime feature**. In particular, the current CLI does not expose `speech-enhancement` through `run stage`; do not manufacture an invocation or run an unsolicited full dub. Follow the skill's API/host-discovery and blocker-reporting rules.

## What was adapted

- Diagnose before processing; choose changes justified by evidence.
- Preserve source media and comparison variants.
- Ask before downloads or changes outside the requested scope.
- Verify output artifacts, timing, measurements, and source preservation.
- Separate perceptual human review from numeric measurements and report remaining defects.

## Trackdub-specific boundaries

- Project and stage evidence take precedence over an ad hoc script's success message.
- Runtime readiness, actual backend, model availability, and stage outcome remain separate.
- Preserve current VAD/diarization versus unprocessed ASR audio routing.
- Do not impose a universal -14 LUFS target, mono conversion, visual upscaling, or new end-user runtime dependencies.
- Do not claim watermarks, end cards, or other upstream features are implemented here.

Use the [cleanup evidence report template](../../.claude/skills/trackdub-media-cleanup/templates/cleanup-report.md) for supplementary review notes. It does not replace registered stage/artifact records.

## Attribution

Adapted from **Video Cleanup by Mehdi Ksibi**, supplied as `Video Cleanup-1.0.1-v1`, associated by its README with `Mehdi-Ks/video-cleanup`. The complete MIT notice is retained with the skill. See [ATTRIBUTION.md](../../.claude/skills/trackdub-media-cleanup/ATTRIBUTION.md), [LICENSE](../../.claude/skills/trackdub-media-cleanup/LICENSE), and [third-party notices](../legal/THIRD_PARTY_NOTICES.md). No Anthropic authorship or endorsement is claimed.
