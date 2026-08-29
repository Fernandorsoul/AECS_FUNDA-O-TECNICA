# ADR-001: Probabilistic Discovery, Deterministic Enforcement

**Status:** Accepted  
**Date:** 2026-08-29

## Context

The AECS controls AI coding agents that produce software changes. LLMs are probabilistic — they can suggest, classify, infer, and explain, but they cannot guarantee correctness. Critical decisions (scope enforcement, budget limits, security policies) must not depend on "one AI saying another AI probably did it right."

## Decision

AECS will use probabilistic models for discovery and reasoning (suggesting policies, classifying risk, selecting context), but all critical enforcement will be deterministic whenever technically possible.

The workflow is:
1. LLM observes a pattern (e.g., "Controllers don't usually access DbContext")
2. LLM suggests it as an architectural rule
3. Human confirms
4. Rule becomes a deterministic policy
5. Future violations are detected without LLM involvement

## Consequences

- Scope enforcement, budget enforcement, and security policies are code, not prompts
- LLM review is one evidence source among many, never the sole authority
- The system can operate even when LLM calls fail
- New rules start as LLM suggestions but graduate to deterministic checks
