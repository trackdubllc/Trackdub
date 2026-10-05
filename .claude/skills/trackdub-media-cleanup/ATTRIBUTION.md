# Attribution and adaptation record

## Upstream

- Work: **Video Cleanup — a Claude plugin**.
- Author/copyright holder: **Mehdi Ksibi**.
- Repository identified by the supplied README: https://github.com/Mehdi-Ks/video-cleanup
- Supplied package directory: `docs/Video Cleanup-1.0.1-v1/`.
- Package revision label: `Video Cleanup-1.0.1-v1`; no upstream commit or independent release verification is claimed.
- Source material: `skills/video-cleanup/SKILL.md`, `README.md`, and `LICENSE` in that supplied package.
- License: MIT, copyright (c) 2026 Mehdi Ksibi. The complete upstream license is retained in [LICENSE](LICENSE).

This skill adapts the upstream diagnose → process → verify discipline, preservation of originals and comparison variants, permission boundaries, conservative processing, timestamped human review, and honest reporting of limitations. It is attributed to the upstream individual author, not to Anthropic. No endorsement by the upstream author or Anthropic is implied.

## Trackdub adaptation

Trackdub-specific changes replace the Python/standalone-CLI recipe with project-spine inspection, real runtime/model readiness checks, stage history, registered artifact provenance, existing SDK/application workflows, and source-preserving verification. The adaptation documents the current enhancement CLI boundary and separate ASR/VAD/diarization audio routing. It does not import the upstream Python scripts, add runtime dependencies, impose -14 LUFS/mono/upscaling defaults, or implement branding/visual-cleanup features.

The adapted skill, this attribution, and the report template are distributed under MIT with the upstream notice retained. Trackdub's unrelated public-core code remains under its existing license. Keep this attribution and the license with redistributed copies or substantial portions of the adaptation. Other runtime binaries and model weights retain their own licenses; this notice grants no rights to them.
