# ADR-002: Modular Monolith as Initial Architecture

**Status:** Accepted  
**Date:** 2026-08-29

## Context

AECS needs multiple bounded contexts (domain, application, infrastructure, verification, control kernel, agent runtime). The temptation is to start with microservices or separate deployables.

## Decision

Start as a modular monolith with clear module boundaries enforced by project references and interfaces. Each module (Domain, Application, Infrastructure, etc.) is a separate .NET project within one solution, deployed as one process.

## Consequences

- Single deployment simplifies the MVP
- Module boundaries are enforced by compilation (project references)
- Future extraction to separate services is possible if needed
- Shared database connection simplifies early development
- No inter-service communication overhead during prototyping
