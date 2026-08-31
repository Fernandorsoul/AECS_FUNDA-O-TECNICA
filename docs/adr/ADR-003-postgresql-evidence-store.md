# ADR-003: PostgreSQL as Operational and Evidence Store

**Status:** Accepted  
**Date:** 2026-08-29

> **Implementation status (2026-08-31):** `PostgreSqlExecutionEvidenceStore` now implements the staged `IExecutionEvidenceStore` contract and is selectable in every CLI flow. JSON remains an explicit local fallback; PostgreSQL selection never falls back silently.

## Context

AECS needs to persist TaskContracts, AgentRuns, VerificationResults, EvidenceEvents, and PolicyDecisions. The data is relational with append-heavy workloads (evidence events).

## Decision

Use PostgreSQL as the single operational and evidence store. Use Entity Framework Core as the ORM. The Evidence Graph will be derived from these tables via queries, not from a separate graph database.

The authenticated execution aggregate is stored as JSONB so the complete versioned payload remains cryptographically identical across backends. Stable task, run, candidate, hash, timestamp and chain fields are relational projections with constraints and indexes. Promotion events are separate append rows linked by FK and sequence. Writers lock the aggregate row and update the event plus signed chain head in one transaction.

The connection string is accepted only from the secret environment variable `AECS_POSTGRES_CONNECTION_STRING`. EF Core migrations create the schema. The CLI keeps `json` for local compatibility, but choosing `postgres` is authoritative and fail-closed.

## Consequences

- Single database simplifies operations and backups
- PostgreSQL handles append-heavy workloads well
- EF Core provides migrations, LINQ queries, and change tracking
- Future vector search (pgvector) is possible for context compilation
- No graph database complexity until the data model proves it necessary
- Database and evidence keyring must be backed up and restored as one operational set
- PostgreSQL improves durability and rollback recovery but is not an independent immutable transparency anchor
