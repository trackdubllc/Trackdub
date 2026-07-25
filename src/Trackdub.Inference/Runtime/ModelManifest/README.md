# src/Trackdub.Inference/Runtime/ModelManifest

## Purpose

Model manifests.

## What belongs here

License metadata, task metadata, hashes, variants.

Current files:

- `model-manifest.schema.json` for the manifest document contract
- `ModelManifest*` types and loaders for parsing and validation
- `ModelManifestLoader` for license/commercial policy validation at load time
- `bundled-models.manifest.json` for committed aliases and benchmark entry points for bundled models

## What should not go here

Binary model files.

## Agent guidance

Keep changes scoped to this directory's purpose. If a task requires crossing boundaries, update the relevant architecture note or ADR first.
