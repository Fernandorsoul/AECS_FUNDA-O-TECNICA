# Registros de decisões arquiteturais

Os ADRs documentam decisões estáveis, o contexto em que foram tomadas e suas consequências. O [Documento de Fundação Técnica](../../AECS_Fundacao_Tecnica_v0.1.md) descreve a tese completa; os ADRs registram escolhas concretas de implementação.

| ADR | Status | Decisão |
| --- | --- | --- |
| [ADR-001](ADR-001-probabilistic-discovery-deterministic-enforcement.md) | Accepted | Usar modelos para descoberta e enforcement determinístico para controles críticos |
| [ADR-002](ADR-002-modular-monolith.md) | Accepted | Iniciar como monólito modular |
| [ADR-003](ADR-003-postgresql-evidence-store.md) | Accepted | Usar PostgreSQL como store operacional e de evidências |
| [ADR-004](ADR-004-docker-isolation.md) | Accepted | Isolar agentes com Docker |
| [ADR-005](ADR-005-local-llm-via-ollama.md) | Accepted | Priorizar modelos locais via Ollama |
| [ADR-006](ADR-006-verification-before-learning.md) | Accepted | Aprender apenas com execuções verificadas |
| [ADR-007](ADR-007-staged-trust-boundary-and-controlled-promotion.md) | Accepted | Isolar descoberta/verificação em worktrees e promover somente por uma ação controlada |
| [ADR-008](ADR-008-authenticated-evidence-envelope.md) | Accepted | Autenticar a evidência e encadear eventos de promoção assinados |
| [ADR-009](ADR-009-preventive-execution-capabilities.md) | Accepted | Aplicar capabilities preventivas versionadas aos comandos staged |
| [ADR-010](ADR-010-reproducible-security-scan-gate.md) | Accepted | Executar SecurityScan reproduzível com baseline, snapshot fixa e saída sanitizada |
| [ADR-011](ADR-011-independent-versioned-test-suite-gates.md) | Accepted | Separar suites unitárias, de integração e aceite com política e evidência TRX próprias |
| [ADR-012](ADR-012-deterministic-repository-snapshot.md) | Accepted | Inventariar a baseline por uma snapshot Git determinística e endereçada por conteúdo |
| [ADR-013](ADR-013-roslyn-msbuild-symbol-graph.md) | Accepted | Usar Roslyn/MSBuild como autoridade semântica para C# e restringir fallback a inventário textual |

## Convenção para novos ADRs

1. Use o próximo número sequencial: `ADR-NNN-titulo-curto.md`.
2. Registre `Status` e `Date` no início.
3. Estruture a decisão em `Context`, `Decision` e `Consequences`.
4. Não reescreva silenciosamente uma decisão histórica. Adicione uma nota de implementação ou crie um ADR que a substitua.
5. Atualize esta tabela no mesmo commit.

Status sugeridos: `Proposed`, `Accepted`, `Deprecated` e `Superseded by ADR-NNN`.
