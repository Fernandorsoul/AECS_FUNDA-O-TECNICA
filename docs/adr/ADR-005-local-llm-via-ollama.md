# ADR-005: Local LLM via Ollama

**Status:** Accepted  
**Date:** 2026-08-29

## Context

AECS needs LLM capabilities for coding agent execution. Cloud APIs (OpenAI, Anthropic) introduce cost, latency, data privacy concerns, and vendor lock-in. The development machine has an AMD RX 570 GPU with limited VRAM.

## Decision

Use Ollama as the local LLM runtime. The adapter communicates via Ollama's REST API (localhost:11434), making it provider-agnostic — switching models is a configuration change, not a code change.

Model strategy by risk level:
- R0/R1: Small models (1.3B-3B) that fit in GPU VRAM
- R2/R3: Larger models (7B) running on CPU when quality matters more than speed

## Consequences

- Zero API cost during development and testing
- No data leaves the machine
- Model quality is limited by hardware (RX 570 = 4GB VRAM)
- Slower inference than cloud APIs, especially for 7B+ models on CPU
- Easy to swap models: `ollama pull codellama:3b` vs `ollama pull deepseek-coder:6.7b`
- Future cloud fallback is possible by implementing a second adapter

## Implementation note — 2026-08-30

The cloud fallback described above has since been implemented. The current CLI keeps Ollama as the primary runtime and, for `run` and `experiment`, can use an OpenAI-compatible `/chat/completions` endpoint when the local execution fails or returns no useful file blocks. Jarvis still uses Ollama or mock directly.

The current model routing also evolved from the initial examples: R0/R1 select `qwen2.5-coder:7b`, while R2–R4 select `qwen2.5-coder:14b`. These are implementation details and may continue to evolve without replacing the architectural decision to prefer a local runtime.
