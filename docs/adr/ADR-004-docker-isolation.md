# ADR-004: Docker-based Agent Isolation

**Status:** Accepted  
**Date:** 2026-08-29

> **Implementation status (2026-08-31):** Implemented for staged repository commands. Build, tests, executable acceptance evidence and external tool probes run in a disposable, resource-limited container with network denied by default. Git control operations and built-in structural policy evaluation remain in the controller. Host execution is an explicit development override and is marked in authenticated evidence.

## Context

AI coding agents execute code, modify files, and run commands. Running them directly on the host machine creates security risks — an agent could access secrets, modify system files, or exfiltrate data.

## Decision

Execute repository-provided code and tools inside Docker containers with:
- a disposable detached Git worktree mounted at `/workspace`;
- no mount of the original checkout, Docker socket, secrets or host caches;
- network mode `none` by default;
- immutable images pinned by digest;
- CPU, memory, PID and wall-clock limits;
- read-only root filesystem, restricted privileges and a bounded temporary filesystem;
- direct structured argv execution, without an intermediary command shell;
- preventive task-authoritative capabilities for writable paths, processes, phases, network, secrets and resource ceilings;
- forced cleanup after success, failure, timeout or cancellation.

## Consequences

- Agent cannot access host secrets or system files
- Each execution starts with a clean environment
- Container lifecycle adds latency (seconds) but improves security
- Docker must be available on the host machine
- Resource limits prevent runaway executions
- Host-only development runs require a second explicit CLI opt-in and are auditable
