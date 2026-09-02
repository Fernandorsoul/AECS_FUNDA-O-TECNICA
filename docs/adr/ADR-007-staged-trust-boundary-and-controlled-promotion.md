# ADR-007: Staged Trust Boundary and Controlled Promotion

**Status:** Accepted
**Date:** 2026-08-30

## Context

A coding agent produces untrusted text. Applying that text directly to a user's checkout before deterministic validation allows the probabilistic runtime to cross the system's write boundary, makes rollback ambiguous, and lets agent-reported metadata become confused with observed repository state.

Verification alone is not sufficient if it runs after the original checkout has already been modified. The system also needs a durable identity for the exact candidate that passed, and a separate authorization point before that candidate can be written to the original repository.

## Decision

AECS will implement the repository trust boundary as a staged Git workflow:

1. Require a clean Git repository and capture its commit, branch, status, root path and timestamp as the baseline.
2. Run baseline build/tests in a disposable detached worktree before invoking an agent.
3. Run context compilation, the agent, file application and candidate verification in a second disposable detached worktree at the same commit.
4. Treat model-supplied paths and file claims as untrusted. Validate paths before writing and derive the authoritative changed-file set and binary diff from Git.
5. Bind the candidate to its task, agent run and baseline, and identify its exact diff with SHA-256.
6. Fail closed when a required verifier is missing, duplicated, skipped, fails, errors, times out or is cancelled. A baseline failure prevents the agent call.
7. Persist execution evidence outside the target repository and revalidate that the original checkout remained unchanged before reporting success.
8. Keep promotion outside the agent execution pipeline. Promotion requires an eligible persisted decision, an explicit actor and approval, the expected diff hash, and an unchanged matching repository.
9. Preflight and apply a promoted patch through Git atomically, verify the resulting staged diff hash, serialize concurrent promotions, and roll back if post-apply validation or audit persistence fails.
10. Support patch export as a separate operation that never writes to the target repository.

The original checkout is therefore immutable during discovery and verification. It becomes writable only inside the explicit controlled-promotion use case.

## Consequences

### Positive

- Rejected, malformed and out-of-scope candidates never modify the original checkout.
- Baseline failures are distinguishable from regressions introduced by a candidate.
- Changed files and diffs are filesystem-derived rather than trusted agent claims.
- Evidence can reproduce which baseline, context, commands, gates and exact patch supported a decision.
- Human or policy authorization is auditable and cannot be self-issued by the agent.
- Concurrent or stale promotions fail closed, and successful promotion leaves a reviewable staged diff without creating a commit.

### Negative and trade-offs

- Execution requires Git, a valid `HEAD`, a stable branch identity (including detached `HEAD`) and a completely clean checkout.
- Two temporary worktrees increase disk use and Git process overhead.
- The current process isolation is a filesystem/Git boundary, not a container boundary; agents and verifiers still execute on the host.
- Both evidence backends are cryptographically signed and atomic; the local keyring is not an HSM/KMS, and neither backend is an independent monotonic anchor against a coordinated rollback.
- Non-cooperating external processes are detected by repeated repository-state validation, not prevented from racing with a staged execution.
- Controlled promotion deliberately does not create a commit or bypass the repository's normal review workflow.

## Relationship to earlier ADRs

- This decision is the concrete enforcement mechanism for [ADR-001](ADR-001-probabilistic-discovery-deterministic-enforcement.md).
- It keeps the modular-monolith boundary from [ADR-002](ADR-002-modular-monolith.md).
- [ADR-003](ADR-003-postgresql-evidence-store.md) provides the selectable durable backend while JSON remains an explicit local fallback.
- [ADR-004](ADR-004-docker-isolation.md) now adds a container boundary around repository commands in each disposable worktree; this ADR remains responsible for Git staging and promotion isolation.
- [ADR-008](ADR-008-authenticated-evidence-envelope.md) authenticates the execution record and its subsequent promotion events in either backend.
