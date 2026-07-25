# Trackdub

Cross-platform, local-first AI dubbing engine, SDK, CLI, and pipeline.

Trackdub automates the speech dubbing pipeline: language detection, speech
recognition (ASR), translation, text-to-speech (TTS), and audio export. All
inference runs locally via ONNX models with no mandatory cloud dependency.

## Components

| Project | Description |
|---------|-------------|
| `Trackdub.Domain` | Pure domain models and value objects |
| `Trackdub.Contracts` | Shared interfaces and DTOs |
| `Trackdub.Application` | Use cases, orchestration, pipeline stages |
| `Trackdub.Infrastructure` | Persistence (SQLite), file I/O, integrations |
| `Trackdub.Media` | Audio/video processing (FFmpeg) |
| `Trackdub.Media.Playback` | libmpv/LibVLC playback surface |
| `Trackdub.Inference` | Model abstractions and pipeline stage contracts |
| `Trackdub.Inference.Onnx` | ONNX Runtime session management and EP registration |
| `Trackdub.Composition` | DI wiring root |
| `Trackdub.Sdk` | Programmatic session API for integrations |
| `Trackdub.Cli` | Headless CLI entry point |
| `Trackdub.Licensing` | Neutral license validation mechanisms |
| `Trackdub.Benchmarks` | Performance benchmarks |
| `Trackdub.Tools` | Development utilities |
| `Trackdub.Analyzers` | Roslyn analyzers |

## Requirements

- .NET 10 SDK (10.0.300+)
- FFmpeg (for audio/video processing)
- libmpv or LibVLC (for playback)

## Build

```bash
dotnet build Trackdub.slnx -m:1
```

## Test

```bash
dotnet test Trackdub.slnx -m:1
```

Tests that require ONNX models or specific fixtures skip cleanly when
dependencies are unavailable.

## Solution Filters

- `Trackdub.Inference.slnx` — Inference, Composition, benchmarks, and tests
- `Trackdub.Sdk.slnx` — SDK, CLI, and SDK tests

## CLI

```bash
dotnet run --project src/Trackdub.Cli -- --help
dotnet run --project src/Trackdub.Cli -- dub --media input.mp4 --target-language es
```

## Architecture

Strict layered dependency direction. Domain depends on nothing:

```
Application → Contracts, Domain, Licensing
Infrastructure → Application, Contracts, Domain
Media → Application, Contracts, Domain
Media.Playback → Application, Domain
Inference → Contracts, Domain
Inference.Onnx → Inference, Contracts, Domain
Composition → Application, Inference, Inference.Onnx, Infrastructure, Licensing, Media, Media.Playback
Sdk → Application, Composition, Licensing
Cli → Sdk
Benchmarks → Application, Composition, Domain, Inference, Inference.Onnx, Infrastructure
Tools → Application, Domain, Infrastructure, Media
Contracts → Domain
Licensing → (nothing)
Domain → (nothing)
```

## Model Governance

Only ONNX models with verified commercial licenses are supported. The bundled
model manifest (`src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`)
is the single source of truth for model inventory.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
