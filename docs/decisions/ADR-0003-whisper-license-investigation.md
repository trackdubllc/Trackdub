# ADR-0003: Whisper (onnx-community) license classification

- Status: Accepted
- Date: 2026-04-29

## Context

`src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`
has two entries for `onnx-community/whisper-tiny` (local bundle and the
`whisper-tiny-onnx` variant).

The previous draft of this ADR kept those entries blocked because the
`onnx-community/whisper-tiny` Hugging Face repo does not publish its own
`license:*` metadata tag. That was a conservative interpretation of AGENTS.md
rule 8: "Do not invent commercial safety - unknown license = unsafe."

The project owner has now made the repo-local policy decision that this
specific artifact should be classified as commercial-safe because it is a
direct ONNX-format conversion of `openai/whisper-tiny`, not a new independently
licensed model family.

## Decision

Classify the bundled `onnx-community/whisper-tiny` artifacts as Apache-2.0 and
commercial-safe.

The accepted manifest values are:

```json
"license": "Apache-2.0",
"commercial_allowed": true,
"redistribution_allowed": true,
"commercial_safe_mode": true
```

This decision applies only to the `onnx-community/whisper-tiny` entries that
identify `openai/whisper-tiny` as their base model and do not add conflicting
license or usage terms. It does not weaken the default rule for unrelated model
artifacts: unknown license still remains unsafe unless a project-specific ADR
or manifest evidence explicitly resolves the artifact.

## Evidence

Current public Hugging Face metadata supports this classification:

- `openai/whisper-tiny` declares `license:apache-2.0` in its Hugging Face tags
  and `cardData.license == "apache-2.0"`.
- `onnx-community/whisper-tiny` identifies `openai/whisper-tiny` as its base
  model through both Hugging Face metadata (`base_model:openai/whisper-tiny`)
  and the model page's model tree.
- The `onnx-community/whisper-tiny` model card describes the repository as ONNX
  weights for compatibility with Transformers.js and links back to
  `openai/whisper-tiny`.
- No non-commercial, custom, or otherwise restrictive license or usage term is
  declared on the `onnx-community/whisper-tiny` repo.

## Policy interpretation

For Trackdub, a format-shifted ONNX artifact can inherit the commercial
classification of its Apache-2.0 base model when all of the following are true:

1. The artifact metadata identifies the Apache-2.0 base model.
2. The artifact is a conversion or quantized conversion of that base model, not
   a separately trained model with unknown provenance.
3. The artifact repository does not declare conflicting or more restrictive
   terms.
4. The manifest entry is covered by an ADR or equivalent repo-local evidence.

If any of those conditions changes, the manifest must be re-reviewed before the
entry is used in commercial-safe mode.

## Consequences

Positive:

- Commercial-safe ASR planning can select the bundled ONNX Whisper-tiny runtime
  path.
- The manifest and policy documentation now agree, so future review bots should
  not treat the commercial-safe flip as an unexplained contradiction.
- The stricter default remains intact for unrelated models.

Negative:

- This decision relies on base-model inheritance for a conversion repo whose HF
  metadata still omits a direct license tag. If the repo later adds explicit
  conflicting terms, Trackdub must update the manifest immediately.

## References

- AGENTS.md rule 8: "Do not invent commercial safety - unknown license =
  unsafe."
- `MODEL_LICENSE_POLICY.md`
- [Hugging Face API: `openai/whisper-tiny`](https://huggingface.co/api/models/openai/whisper-tiny)
- [Hugging Face API: `onnx-community/whisper-tiny`](https://huggingface.co/api/models/onnx-community/whisper-tiny)
- [Hugging Face model page: `onnx-community/whisper-tiny`](https://huggingface.co/onnx-community/whisper-tiny)
