# ADR-003: PostgreSQL as Operational and Evidence Store

**Status:** Accepted  
**Date:** 2026-08-29

> **Implementation status (2026-08-30):** the staged CLI currently persists complete execution evidence in a signed, versioned JSON envelope and appends signed promotion events through `JsonExecutionEvidenceStore` outside the target repository. PostgreSQL remains the accepted operational target, but `AecsDbContext`/`EvidenceStore` are not wired into the CLI composition root yet.

## Context

AECS needs to persist TaskContracts, AgentRuns, VerificationResults, EvidenceEvents, and PolicyDecisions. The data is relational with append-heavy workloads (evidence events).

## Decision

Use PostgreSQL as the single operational and evidence store. Use Entity Framework Core as the ORM. The Evidence Graph will be derived from these tables via queries, not from a separate graph database.

## Consequences

- Single database simplifies operations and backups
- PostgreSQL handles append-heavy workloads well
- EF Core provides migrations, LINQ queries, and change tracking
- Future vector search (pgvector) is possible for context compilation
- No graph database complexity until the data model proves it necessary
