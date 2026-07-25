# ADR-0011: Intentional Trackdub.Contracts → Trackdub.Domain project reference

- Status: Accepted
- Date: 2026-06-02

## Context

The repository's layering guidance treats `Trackdub.Domain` as the innermost
project with no outward dependencies. `Trackdub.Contracts` was introduced as a
shared contract surface for pipeline stages, execution snapshots, and
cross-layer DTOs.

Over time, contract types began reusing domain value objects, enums, and
records directly instead of duplicating parallel contract-only shapes. That
reuse is encoded as a real `ProjectReference` from `Trackdub.Contracts` to
`Trackdub.Domain` in `src/Trackdub.Contracts/Trackdub.Contracts.csproj`.

This coupling predates the stricter "Contracts has zero project dependencies"
wording that appeared in CI prompts, `CLAUDE.md`, and some review checklists.
The canonical dependency diagram in `AGENTS.md`, the architecture test
`ContractsReferencesOnlyDomain`, and this ADR now describe the **actual**
allowed edge: Contracts may depend on Domain and on nothing else.

Removing the reference today would touch 30+ files across Contracts,
Application, Inference, and tests. That is true architectural debt, not a
quick hygiene fix.

## Decision

Keep the `Trackdub.Contracts → Trackdub.Domain` project reference as an
**intentional, documented exception** to the "Domain is the only leaf" ideal.

Enforcement rules:

- `Trackdub.Domain` remains dependency-free.
- `Trackdub.Contracts` may reference **only** `Trackdub.Domain`.
- No other project may treat Contracts as a substitute for Domain when pure
  domain invariants are required; Domain stays the source of pipeline truth.

CI, the dependency-graph audit script, and architecture tests must all encode
this rule consistently. Stale "zero deps on Contracts" checks are bugs in
documentation/automation, not signals to delete the reference without a
planned refactor.

## Consequences

Positive:

- Contract types stay aligned with domain invariants instead of drifting
  duplicate models.
- The allowed edge is explicit, testable, and reviewable.
- Agents and contributors stop fighting a fictional zero-dependency Contracts
  project.

Negative:

- Contracts is no longer a pure outward-facing leaf; it cannot be published or
  reused independently of Domain without pulling Domain types along.
- The ideal DDD onion (Contracts above Domain with no back-edge) remains
  unmet until a deliberate extraction effort lands.

## Alternatives considered

### Remove the reference now and duplicate types in Contracts

Rejected for this milestone: large, risky churn across many files with little
immediate product value and high merge conflict risk on active branches.

### Move shared types into Contracts and delete Domain usage from Contracts

Rejected as a single PR for the same scope reason. Some shared shapes are
genuinely domain entities/value objects; blindly moving them would blur the
Domain/Contracts boundary in the opposite direction.

### Introduce a SharedKernel (or similar) project below Contracts

Preferred **future** remediation path when the team budgets a focused refactor:

1. Identify types referenced by both Contracts and Domain consumers.
2. Extract stable, dependency-free primitives into `Trackdub.SharedKernel`
   (name TBD) or fold portable primitives into Contracts without referencing
   full Domain entities.
3. Rewire Contracts to depend on SharedKernel only; Domain may also depend on
   SharedKernel for shared primitives.
4. Delete `Contracts → Domain` once Contracts compiles without Domain types.

Until that work ships, the existing reference stays.

## Remediation checklist (future)

- Inventory Contracts files that `using Trackdub.Domain` or reference domain
  entity types.
- Classify each usage: true domain invariant vs. portable DTO/enums.
- Extract portable primitives to SharedKernel or duplicate thin contract DTOs
  where duplication is cheaper than shared entity coupling.
- Update `AGENTS.md`, architecture tests, and `verify-dependency-graph.py`
  if the graph changes.
- Remove the project reference only when `ContractsReferencesOnlyDomain` can
  be replaced by `ContractsHasNoProjectReferences` (or SharedKernel-only).

## References

- `tests/Trackdub.Architecture.Tests/DependencyGraphTests.cs` —
  `ContractsReferencesOnlyDomain`
- `tools/ci/verify-dependency-graph.py` — canonical allowed edges
- `AGENTS.md` — dependency diagram (`Contracts → Domain`)
