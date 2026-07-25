# ADR-0006: Chatterbox Commercial Use Verification

- Status: Superseded
- Date: 2026-04-29
- Superseded: 2026-05-09 by the manifest hash-integrity rule in `MODEL_LICENSE_POLICY.md`

## Context

Milestone 15 uses Chatterbox ONNX models for consent-gated voice cloning. Commercial-safe mode only allows model routes whose manifests declare `commercial_use_verified: true`.

Current implementation note: `commercial_use_verified: true` now requires both
commercial-use license confidence and a non-empty SHA-256 for artifact integrity.
The Chatterbox entries may still be likely commercial-safe by license review, but
they must not be routed in commercial-safe mode until their manifest entries have
verified hashes.

The Chatterbox ONNX repositories used by Trackdub currently declare MIT licenses on their Hugging Face model pages:

- `ResembleAI/chatterbox-turbo-ONNX`: https://huggingface.co/ResembleAI/chatterbox-turbo-ONNX
- `onnx-community/chatterbox-ONNX`: https://huggingface.co/onnx-community/chatterbox-ONNX

The model cards also document the expected ONNX graph package layout used by the Trackdub downloader and Chatterbox wrapper.

## Decision

Trackdub treats the two Chatterbox ONNX entries as license candidates that
must remain blocked from commercial-safe routing until artifact hashes are
verified:

- `ResembleAI/chatterbox-turbo-ONNX`
- `onnx-community/chatterbox-ONNX`

Both entries remain voice-cloning models. They must continue to set:

- `voice_cloning: true`
- `requires_user_consent: true`

Commercial-safe mode must not route to these entries while `commercial_use_verified`
is false. If they are later restored to `commercial_use_verified: true`, the
per-session voice-cloning consent gate remains mandatory and non-bypassable.

## Consequences

Positive:

- The ADR records the upstream MIT license evidence that made Chatterbox a plausible commercial-safe candidate.
- The stricter manifest gate avoids presenting license confidence as full commercial readiness before artifact integrity is verified.

Negative:

- The manifest now depends on both upstream license declarations and local artifact hashes. If either source changes license or adds restrictions, the manifest must be revised immediately.
- Commercial-use verification does not verify consent from a voice subject and does not reduce the user's legal responsibility for voice cloning.
