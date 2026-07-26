# Contributing

Trackdub (this repository) is the Apache-2.0 public core: the SDK, CLI,
pipeline, inference, media processing, infrastructure, and neutral licensing
mechanisms. See [docs/repository-policy.md](docs/repository-policy.md) and
[AGENTS.md](AGENTS.md) for organization, dependency-direction rules, and
build/test commands before opening a change.

## Inbound contribution terms

- Contributions to this repository are submitted under the
  [Apache License, Version 2.0](LICENSE).
- By submitting a contribution, you certify that you have the right to submit
  it under that license (for example, it is your own original work, or you
  otherwise hold the necessary rights).
- No separate Contributor License Agreement (CLA) is currently required to
  contribute here.
- This forward-looking contribution policy is separate from, and does not
  affect, any historical intellectual-property ownership review of code
  already in the repository.

## Before submitting a change

- Run `dotnet build Trackdub.slnx -m:1` and `dotnet test Trackdub.slnx -m:1`.
- Run `dotnet format Trackdub.slnx --verify-no-changes`.
- Follow the coding style and dependency-direction rules in
  [AGENTS.md](AGENTS.md).
- Use imperative commit titles: `Add ...`, `Fix ...`, `Remove ...`.
