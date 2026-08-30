# ADR-004: Docker-based Agent Isolation

**Status:** Accepted  
**Date:** 2026-08-29

> **Implementation status (2026-08-30):** Docker sandbox infrastructure exists, but the default staged CLI executes agents and deterministic verifiers on the host. Disposable Git worktrees provide repository-write isolation today; they do not replace the container boundary described by this ADR.

## Context

AI coding agents execute code, modify files, and run commands. Running them directly on the host machine creates security risks — an agent could access secrets, modify system files, or exfiltrate data.

## Decision

Execute coding agents inside Docker containers with:
- Temporary workspace (copy of the repository)
- No access to host filesystem beyond the mounted workspace
- Network restrictions (only Ollama API access)
- Configurable timeout and resource limits
- Disposable containers (created per execution, destroyed after)

## Consequences

- Agent cannot access host secrets or system files
- Each execution starts with a clean environment
- Container lifecycle adds latency (seconds) but improves security
- Docker must be available on the host machine
- Resource limits prevent runaway executions
