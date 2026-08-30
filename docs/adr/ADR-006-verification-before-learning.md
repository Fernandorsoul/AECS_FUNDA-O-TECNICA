# ADR-006: Verification Before Adaptive Learning

**Status:** Accepted  
**Date:** 2026-08-29

## Context

The AECS roadmap includes an Adaptive Controller that learns from execution history to improve model selection, context strategy, and budget allocation. There is a temptation to start with ML/heuristics from day one.

## Decision

Implement deterministic verification first. The Adaptive Controller will only be introduced after:
1. The verification pipeline is proven and stable
2. Sufficient execution data is collected (50+ runs)
3. Simple heuristics are tested before any ML approach

The learning progression will be: Rules → Heuristics → Telemetry → Dataset → Statistical Models → Contextual Bandits → (possible RL)

## Consequences

- The system is predictable and debuggable from day one
- No "cold start" problem for verification (it works immediately)
- The Adaptive Controller has real data to learn from when introduced
- Premature ML complexity is avoided
- Each learning stage can be validated before advancing
