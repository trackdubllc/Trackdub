# src/Trackdub.Benchmarks

## Purpose

Benchmark harness.

## What belongs here

Console and in-app benchmark core.

## What should not go here

Production UI or project-specific artifacts.

## Stage-focused matrix

The `controlled-matrix` command runs the existing controlled benchmark once for
each selected pipeline stage and writes a matrix report containing each stage's
`BenchmarkEvidenceReport`:

```bash
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- \
  controlled-matrix fixture.mp4 \
  --output benchmark-matrix \
  --stages vad,diarization,asr,translation,tts,export
```

With no `--stages`, the command uses the canonical extended stage catalog. Use
`--model stage=alias` to pin a model for an individual stage. The matrix keeps
stage-level evidence separate from the BDN microbenchmark artifacts.

## Agent guidance

Keep changes scoped to this directory's purpose. If a task requires crossing boundaries, update the relevant architecture note or ADR first.
