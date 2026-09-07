# AECS — Agentic Engineering Control System
## Documento de Fundação Técnica — v0.1

**Status:** Tese consolidada / pronto para iniciar prototipação  
**Objetivo:** servir como documento-base para implementação do primeiro protótipo do AECS.

> **Vigência:** este é um registro fundacional v0.1, não um inventário do código atual. Em
> particular, a menção a .NET 10 registra a direção originalmente pretendida e agora implementada.
> A implementação vigente usa projetos `net10.0` e SDK/sandbox .NET 10 LTS; consulte a
> [matriz de suporte .NET](docs/dotnet-support.md) e o
> [ADR-020](docs/adr/ADR-020-dotnet-support-matrix.md).

---

# 1. Visão

O AECS é um **sistema de controle adaptativo para engenharia de software agêntica**.

A proposta não é criar “mais um Copilot”, “mais um agente que escreve código” ou “mais um linter com IA”.

O objetivo é construir uma camada independente entre:

- o desenvolvedor;
- os agentes de programação;
- os modelos de IA;
- o repositório;
- ferramentas de desenvolvimento;
- CI/CD;
- políticas;
- verificadores;
- produção.

O AECS deve permitir que um engenheiro humano supervisione uma força de trabalho de agentes de IA sem perder:

- controle;
- entendimento;
- segurança;
- arquitetura;
- previsibilidade;
- rastreabilidade;
- qualidade;
- orçamento.

A experiência de usuário poderá assumir a forma de um assistente semelhante ao conceito de **Jarvis**, porém o “Jarvis” será apenas a interface cognitiva do sistema.

O núcleo real será um sistema de controle independente e majoritariamente determinístico.

---

# 2. Tese central

> **À medida que software passa a ser produzido por agentes, empresas precisarão de uma camada independente que transforme intenção, execução e resultado em evidência verificável, utilizando esse histórico para melhorar continuamente custo, qualidade e controle.**

A pergunta principal deixa de ser:

> “Qual IA escreve o melhor código?”

e passa a ser:

> **“Como controlar, medir e melhorar uma força de trabalho de agentes que produz software mais rápido do que humanos conseguem inspecioná-lo?”**

---

# 3. Hipótese de pesquisa

> **Como projetar um sistema de controle adaptativo para engenharia de software agêntica que maximize mudanças verificadamente corretas, minimizando custo total, intervenção humana e risco, sob invariantes arquiteturais e de segurança?**

---

# 4. O que o AECS NÃO é

O AECS não deve ser projetado como:

- chatbot de programação;
- clone do GitHub Copilot;
- clone do Cursor;
- gerador genérico de código;
- simples AI code reviewer;
- linter baseado apenas em LLM;
- roteador de modelos baseado somente em preço;
- ferramenta cujo diferencial seja “economizar tokens”;
- IDE proprietária;
- sistema dependente de um único fornecedor de IA.

Essas áreas já estão rapidamente se comoditizando.

---

# 5. O que o AECS É

O AECS é uma combinação de:

- control plane;
- observabilidade;
- policy enforcement;
- execução isolada;
- memória estruturada;
- grafo de evidências;
- análise de arquitetura;
- semantic linting;
- seleção de contexto;
- roteamento de modelos;
- gerenciamento de budget;
- verificação;
- aprendizado longitudinal.

Sua responsabilidade principal é:

> **supervisionar as IAs que programam.**

---

# 6. Princípio arquitetural fundamental

## Probabilistic Discovery. Deterministic Enforcement.

Modelos probabilísticos podem:

- sugerir;
- classificar;
- inferir;
- detectar padrões;
- explicar;
- levantar hipóteses;
- propor políticas.

Mas decisões críticas devem, sempre que possível, ser convertidas em mecanismos determinísticos.

Exemplo:

1. LLM percebe que Controllers normalmente não acessam `DbContext`.
2. LLM sugere que isso seja uma regra arquitetural.
3. Humano confirma.
4. A regra vira policy.
5. Futuras violações são detectadas deterministicamente.

O sistema não deve depender de:

> “uma IA dizendo que outra IA provavelmente fez certo”.

---

# 7. Analogia com teoria de controle

O AECS pode ser modelado como um sistema de controle.

## Setpoint

Intenção humana.

Exemplo:

> Implementar recuperação de senha.

## Plant

- agente;
- modelo;
- repositório;
- ferramentas;
- terminal;
- CI;
- ambiente.

## Controller

AECS.

## Actuators

- modelo escolhido;
- estratégia de contexto;
- ferramentas disponíveis;
- budget;
- retries;
- profundidade de execução;
- verificações;
- agente selecionado.

## Sensors

- compilador;
- testes;
- linters;
- Roslyn;
- análise estática;
- policy engine;
- segurança;
- CI;
- observabilidade;
- feedback humano;
- sinais de produção.

## Feedback loop

```text
Human Intent
    ↓
Task Contract
    ↓
Controller
    ↓
Agent Execution
    ↓
Verification
    ↓
Evidence
    ↓
Outcome
    ↓
Learning
    ↓
Controller
```

---

# 8. Arquitetura conceitual

```text
                    HUMAN
                      │
                      ▼
                    JARVIS
           reasoning + interaction
                      │
                      ▼
              AECS CONTROL PLANE
                      │
        ┌─────────────┼─────────────┐
        ▼             ▼             ▼
    TaskContract   Risk Engine   Controller
        │             │             │
        └─────────────┼─────────────┘
                      ▼
                Context Compiler
                      │
                Model / Tool Router
                      │
                      ▼
                  Agent Runtime
                      │
                      ▼
                 Control Kernel
                      │
       ┌──────────────┼───────────────┐
       ▼              ▼               ▼
    Policies        Budget          Sandbox
       │              │               │
       └──────────────┼───────────────┘
                      ▼
                    Patch
                      │
                      ▼
               Verification DAG
                      │
       ┌──────────────┼───────────────┐
       ▼              ▼               ▼
    Compile          Tests          Security
       │              │               │
    Architecture    Contracts       Secrets
       │              │               │
       └──────────────┼───────────────┘
                      ▼
                   Evidence
                      │
                ┌─────┴─────┐
                ▼           ▼
             Accept       Reject
                │           │
                ▼           ▼
              Merge        Retry
                │
                ▼
            Production
                │
                ▼
          Evidence Graph
                │
                ▼
       Adaptive Controller
```

---

# 9. Jarvis

“Jarvis” é a camada de interação humana.

Ele deve:

- explicar decisões;
- alertar quando necessário;
- resumir estado;
- receber intenção;
- apresentar riscos;
- pedir autorização;
- recomendar estratégia;
- permitir investigação do histórico.

Ele NÃO deve:

- ser o único policy engine;
- controlar permissões sozinho;
- decidir segurança somente por LLM;
- executar ações críticas sem autorização adequada;
- interromper o desenvolvedor constantemente.

## Princípio de UX

> **Jarvis precisa merecer cada interrupção.**

Níveis sugeridos:

- SEV-1 → interrompe imediatamente;
- SEV-2 → notificação;
- SEV-3 → acumula;
- SEV-4 → mostra apenas sob demanda.

---

# 10. TaskContract

Toda tarefa relevante deve ser transformada em um contrato estruturado antes da execução.

Exemplo:

```yaml
task:
  id: AUTH-482

  objective:
    implement password recovery

  acceptance:
    - user can request reset
    - token expires after configured duration
    - token can only be used once

  scope:
    allowed:
      - src/Identity/**
      - tests/Identity/**

    forbidden:
      - src/Billing/**
      - infrastructure/terraform/**

  constraints:
    security_risk: high
    database_migration: false
    external_dependency: false

  budget:
    tokens: 60000
    usd: 1.50
    retries: 3
    wall_clock_seconds: 900

  verification:
    compile: required
    unit_tests: required
    integration_tests: required
    security_scan: required
    architecture: required

  approval:
    production: human
```

O TaskContract deve permitir detectar:

- scope drift;
- ações não autorizadas;
- budget excedido;
- alterações inesperadas;
- necessidade de aprovação humana;
- falha em critérios de aceite.

---

# 11. Verified Correct Change — VCC

Software arbitrário não permite provar facilmente correção absoluta.

Por isso o AECS trabalhará inicialmente com a noção de:

## Verified Correct Change

Uma mudança considerada suficientemente sustentada por evidências.

Modelo inicial:

```text
VCC = I ∧ B ∧ A ∧ S ∧ T ∧ P
```

Onde:

- `I` = intenção atendida;
- `B` = build válido;
- `A` = invariantes arquiteturais satisfeitos;
- `S` = invariantes de segurança satisfeitos;
- `T` = testes necessários passaram;
- `P` = políticas satisfeitas.

Para alterações críticas:

```text
VCC = I ∧ B ∧ A ∧ S ∧ T ∧ P ∧ H
```

Onde:

- `H` = aprovação humana.

---

# 12. Risk Engine

A intensidade da verificação deve depender do risco.

## R0

- documentação;
- comentários;
- formatação.

## R1

- UI;
- DTO;
- CRUD simples.

## R2

- regras de negócio;
- acesso a dados;
- contratos de API;
- fluxos importantes.

## R3

- autenticação;
- autorização;
- pagamentos;
- migrations;
- infraestrutura;
- criptografia;
- secrets;
- permissões.

Quanto maior o risco:

- maior o contexto permitido;
- maior o budget;
- modelos mais capazes;
- mais verificadores;
- maior necessidade de aprovação humana.

---

# 13. Métrica econômica principal

Não otimizar:

```text
Cost per Token
```

O AECS deve otimizar:

# Cost per Verified Change — CPVC

```text
CPVC = C_total / N_verified_changes
```

Onde:

```text
C_total =
    C_LLM
  + C_compute
  + C_CI
  + C_review
  + C_retry
  + C_rework
  + C_incident
```

Outras métricas obrigatórias:

- First-pass verification rate;
- Human minutes / change;
- Retries / change;
- Escaped defects;
- Post-merge rework;
- Architecture violations;
- Security violations;
- Policy violations;
- Agent interventions;
- AI spend / merged PR;
- rollback rate;
- incident rate;
- median task latency.

---

# 14. Adaptive Controller

O controlador escolhe estratégia de execução.

Estado observado pode conter:

```json
{
  "language": "csharp",
  "taskType": "bugfix",
  "complexity": 0.18,
  "risk": "low",
  "affectedFiles": 2,
  "affectedModules": 1,
  "testCoverage": 0.81,
  "historicalFailureRate": 0.04
}
```

O controlador pode escolher:

- modelo;
- contexto;
- ferramentas;
- budget;
- verification profile;
- retry policy.

Formalmente:

```text
action = (
    Model,
    ContextStrategy,
    Tools,
    Budget,
    VerificationStrategy,
    RetryStrategy
)
```

Objetivo:

```text
minimize ExpectedTotalCost(action)
```

sujeito a:

```text
P(VCC | action, state) >= threshold
```

---

# 15. Estratégia de aprendizado

Não iniciar com reinforcement learning.

Ordem proposta:

```text
Rules
  ↓
Heuristics
  ↓
Telemetry
  ↓
Dataset
  ↓
Statistical Models
  ↓
Contextual Bandits
  ↓
Possível RL futuro
```

Primeiros candidatos futuros:

- Thompson Sampling;
- LinUCB;
- contextual bandits.

---

# 16. Context Compiler

Objetivo:

> selecionar o menor conjunto de informações capaz de aumentar significativamente a probabilidade de sucesso da tarefa.

Nunca assumir que:

> mais contexto = melhor resultado.

Pipeline:

```text
Task
 ↓
Seed Symbols
 ↓
Graph Expansion
 ↓
Historical Relevance
 ↓
Policy Relevance
 ↓
Test Relevance
 ↓
Token Budget
 ↓
Context Package
```

Exemplo:

```text
12 relevant symbols
3 architectural rules
4 tests
2 previous changes
1 previous incident
```

Cada pacote deve possuir identificador próprio:

```text
CTX-af73c912
```

Isso permite comparar estratégias de contexto empiricamente.

---

# 17. Context Utility Density

Métrica experimental proposta:

```text
CUD = ΔP(VCC) / context_tokens
```

Objetivo:

medir quanta melhoria de resultado cada unidade de contexto realmente oferece.

---

# 18. Engineering Evidence Graph

O coração informacional do sistema.

Não deve armazenar apenas relações estáticas do código.

Deve armazenar:

```text
Task
 ↓
Agent
 ↓
Model
 ↓
Context
 ↓
Tools
 ↓
Execution
 ↓
Files Changed
 ↓
Tests
 ↓
Review
 ↓
PR
 ↓
Merge
 ↓
Release
 ↓
Production Outcome
```

Exemplo:

```text
Task AUTH-482
    │
    ├─ classified_as → SecurityCritical
    ├─ executed_by → AgentRun-921
    └─ produced → Patch-812
                       │
                       ├─ touched → File A
                       ├─ touched → File B
                       ├─ verified_by → Test C
                       └─ merged_as → PR #1221
                                         │
                                         ▼
                                     Release 51
                                         │
                                         ▼
                                     Production
```

---

# 19. AgentRun

Cada execução deve registrar, no mínimo:

```text
agent_run_id
task_id
agent
model
provider
prompt_hash
context_package_id
tools
token_input
token_output
cached_tokens
estimated_cost
actual_cost
duration
retries
commands
files_touched
policy_decisions
verification_results
exit_reason
```

---

# 20. Tipos de memória

O AECS não deve tratar toda memória como verdade.

Tipos iniciais:

## Fact

Exemplo:

```text
PaymentService calls StripeGateway.
```

Autoridade: alta.

## Explicit Decision

Exemplo:

```text
ADR-17 requires Result<T>.
```

Autoridade: muito alta.

## Policy

Exemplo:

```text
Domain cannot depend on Infrastructure.
```

Autoridade: máxima.

## Observation

Exemplo:

```text
Most services use Result<T>.
```

Autoridade: média.

## Hypothesis

Exemplo:

```text
This module may be deprecated.
```

Autoridade: baixa.

## Preference

Exemplo:

```text
Prefer records for DTOs.
```

Autoridade: variável.

Toda memória importante deve apontar para evidência.

---

# 21. Semantic Engineering Linter

O linter será uma feature do AECS, não o produto inteiro.

Primeiros tipos:

## EB001 — Architecture Violation

Detecta violações formais da arquitetura.

## EB002 — Existing Pattern Violation

Detecta implementação inconsistente com padrões conhecidos.

## EB003 — Possible Breaking Change

Detecta alterações com impacto em consumidores.

## EB004 — Missing Related Change

Detecta arquivos, testes, contratos ou documentação provavelmente esquecidos.

## EB005 — Historical Decision Conflict

Detecta conflitos com ADRs, decisões ou incidentes anteriores.

Outros futuros:

- scope lint;
- business rule lint;
- test impact lint;
- security lint;
- dependency lint;
- complexity drift;
- AI-generated technical debt.

---

# 22. Engineering Entropy

Possível indicador futuro da saúde estrutural do projeto.

Sinais:

- architecture drift;
- duplication;
- dependency growth;
- test debt;
- dead code;
- cyclic dependencies;
- increasing module coupling;
- failed agent attempts;
- rework rate.

Exemplo:

```text
Repository Health

Architecture drift        +13%
Duplicate patterns         +8%
Test coverage risk         +4%
Dependency complexity     +11%

Engineering Entropy:
71/100
```

Esse indicador NÃO deve ser lançado até existir uma base empírica razoável.

---

# 23. Policy Engine

Preferência inicial:

- Open Policy Agent;
- Rego.

Exemplo:

```rego
package agent.filesystem

default allow := false

allow if {
    input.operation == "write"
    startswith(input.path, "src/Identity/")
}
```

O LLM pode sugerir policy.

O humano deve aprovar quando a policy produzir enforcement relevante.

---

# 24. Control Kernel

O kernel deve ser determinístico sempre que possível.

Responsabilidades:

- capability enforcement;
- filesystem scope;
- network scope;
- budget enforcement;
- retry limits;
- timeout;
- execution limits;
- approval gates;
- secrets access;
- policy decisions;
- sandbox lifecycle.

Princípio:

```text
Agent != Authority
```

O agente nunca determina sozinho seus próprios limites.

---

# 25. Agent Circuit Breaker

Limites mínimos:

```text
MAX_TOKENS
MAX_COST
MAX_FILES_CHANGED
MAX_RETRIES
MAX_EXECUTION_DEPTH
MAX_CONTEXT
MAX_DURATION
```

Exemplo:

```text
Agent stopped.

Reason:
Budget exceeded.

Budget:
$0.50

Consumed:
$0.49

Progress:
42%

Recommended:
Re-plan task.
```

---

# 26. Verification DAG

Não usar um único “LLM verifier”.

Criar múltiplos verificadores independentes.

```text
Patch
  │
  ├── Compile
  ├── Unit Tests
  ├── Integration Tests
  ├── Architecture Rules
  ├── Contract Tests
  ├── Static Analysis
  ├── Secret Scan
  ├── Security Scan
  ├── Scope Validation
  └── Semantic Verifier
```

Cada evidência terá autoridade diferente.

Exemplo:

| Evidence | Nature |
|---|---|
| Compilation | deterministic |
| Unit test | high confidence |
| Integration test | high confidence |
| Architecture policy | deterministic |
| Security policy | deterministic |
| Static analysis | high confidence |
| LLM review | probabilistic |
| Pattern inference | probabilistic |
| Human approval | authoritative decision |

---

# 27. Change Attestation

Cada mudança gerada por agente poderá futuramente possuir uma attestation.

Exemplo conceitual:

```text
task
model
context
tools
policy decisions
patch hash
verification
human approval
timestamp
signature
```

Objetivo:

- provenance;
- auditabilidade;
- reconstrução da execução;
- governança;
- cadeia de evidências.

---

# 28. Observability

Toda execução deverá ser rastreável como trace.

Exemplo:

```text
Task
└── AgentRun
    ├── ContextBuild
    ├── LLMCall
    ├── ToolCall
    │   └── git
    ├── LLMCall
    ├── ToolCall
    │   └── tests
    ├── Verification
    └── Result
```

Preferir OpenTelemetry.

Eventos principais:

```text
task.created
task.compiled
agent.started
llm.called
tool.called
policy.evaluated
budget.updated
file.modified
verification.started
verification.completed
agent.stopped
patch.accepted
patch.rejected
pr.created
pr.merged
release.created
incident.linked
```

---

# 29. Segurança

O AECS será um alvo de alto valor.

Princípios obrigatórios:

```text
local-first where possible
least privilege
deny by default
ephemeral credentials
sandboxed execution
signed actions
immutable audit
explicit capabilities
network restrictions
secret isolation
```

Nunca dar ao agente:

- credenciais permanentes;
- acesso irrestrito ao host;
- acesso irrestrito à rede;
- acesso automático a produção;
- poder para ampliar suas próprias permissões.

---

# 30. Arquitetura física inicial

## Core

```text
.NET 10
ASP.NET Core
```

## C# Analysis

```text
Roslyn
```

## Outras linguagens futuramente

```text
Tree-sitter
Language Servers
```

## Banco operacional

```text
PostgreSQL
```

## Vetores

```text
pgvector
```

## Telemetria

```text
OpenTelemetry
```

## Runtime

```text
Docker
```

## Policy

```text
OPA / Rego
```

## Frontend futuro

```text
Angular
```

## Editor client

```text
VS Code Extension / TypeScript
```

A extensão será cliente do sistema, nunca o núcleo.

---

# 31. Evitar complexidade prematura

Não usar inicialmente:

- Kubernetes;
- Neo4j;
- Kafka;
- microservices;
- custom ML;
- reinforcement learning;
- multi-region;
- voz;
- vários agentes concorrentes;
- dezenas de linguagens;
- dezenas de modelos.

Começar modularmente em um **modular monolith**.

---

# 32. Estrutura inicial de solução

Sugestão:

```text
aecs/
│
├── src/
│   ├── AECS.Api/
│   ├── AECS.Application/
│   ├── AECS.Domain/
│   ├── AECS.Infrastructure/
│   ├── AECS.ControlKernel/
│   ├── AECS.AgentRuntime/
│   ├── AECS.Verification/
│   ├── AECS.ContextCompiler/
│   ├── AECS.EvidenceGraph/
│   ├── AECS.PolicyEngine/
│   └── AECS.Observability/
│
├── tests/
│   ├── AECS.UnitTests/
│   ├── AECS.IntegrationTests/
│   └── AECS.SystemTests/
│
├── agents/
│   └── adapters/
│
├── policies/
│
├── experiments/
│
├── docs/
│   ├── adr/
│   ├── architecture/
│   └── research/
│
└── README.md
```

---

# 33. Interfaces essenciais

Primeiro conjunto conceitual:

```csharp
public interface IAgentAdapter
{
    Task<AgentRunResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IVerifier
{
    Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IPolicyEngine
{
    Task<PolicyDecision> EvaluateAsync(
        PolicyInput input,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IContextCompiler
{
    Task<ContextPackage> CompileAsync(
        TaskContract task,
        RepositorySnapshot repository,
        ContextBudget budget,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IExecutionController
{
    Task<ExecutionPlan> PlanAsync(
        TaskState state,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IEvidenceStore
{
    Task AppendAsync(
        Evidence evidence,
        CancellationToken cancellationToken);
}
```

---

# 34. Primeiras entidades de domínio

## TaskContract

```text
Id
Objective
AcceptanceCriteria
Scope
Constraints
Budget
Risk
VerificationProfile
ApprovalPolicy
Status
```

## AgentRun

```text
Id
TaskId
AgentType
Model
Provider
ContextPackageId
StartedAt
FinishedAt
TokenUsage
Cost
RetryCount
ExitReason
```

## ContextPackage

```text
Id
TaskId
Strategy
TokenBudget
SelectedFiles
SelectedSymbols
Policies
HistoricalEvidence
Tests
TokenCount
```

## Evidence

```text
Id
TaskId
AgentRunId
Type
Source
Payload
Authority
Confidence
CreatedAt
```

## VerificationResult

```text
Id
AgentRunId
Verifier
Status
Severity
Evidence
Duration
```

## PolicyDecision

```text
Id
AgentRunId
Policy
Action
Decision
Reason
Timestamp
```

---

# 35. MVP — AECS v0.1

O primeiro MVP deve provar **controle**, não “inteligência geral”.

Escopo:

1. aceitar uma tarefa;
2. gerar TaskContract;
3. classificar risco básico;
4. selecionar um único AgentAdapter;
5. executar agente em Docker;
6. impor:
   - budget;
   - timeout;
   - scope de arquivos;
7. registrar telemetria;
8. capturar diff;
9. executar:
   - build;
   - unit tests;
   - scope validation;
10. criar Evidence;
11. decidir:
   - ACCEPT;
   - REJECT;
   - HUMAN_REVIEW;
12. persistir AgentRun e resultados.

Sem Jarvis sofisticado inicialmente.

CLI é suficiente.

---

# 36. Primeira experiência de uso

Exemplo:

```bash
aecs run \
  --repo ./sample \
  --task "Fix null handling in CustomerMapper"
```

Saída:

```text
AECS TASK #001

Risk:
R1

Allowed scope:
src/Customers/**
tests/Customers/**

Selected strategy:
Model: provider/model-x
Budget: $0.20
Retries: 1

Agent execution started...

Files changed:
2

Verification:
[PASS] Build
[PASS] Unit tests
[PASS] Scope
[PASS] Architecture

Estimated AI cost:
$0.08

Human review:
Recommended

RESULT:
VERIFIED
```

---

# 37. O primeiro experimento científico

Selecionar:

```text
50 tarefas reais
```

preferencialmente C#.

Categorias:

- simple bugfix;
- CRUD;
- business logic;
- refactoring;
- architecture;
- tests;
- API contract;
- security-sensitive.

Executar com:

```text
small model
medium model
frontier model
```

e comparar:

```text
without Context Compiler
vs
with Context Compiler
```

Registrar:

- tokens;
- cost;
- duration;
- retries;
- build result;
- test result;
- files modified;
- human review time;
- VCC result;
- rework.

---

# 38. Hipóteses a testar

## H1 — Context Compiler

Contexto selecionado melhora:

```text
VCC / total cost
```

comparado a contexto ingênuo.

## H2 — Adaptive Routing

Nem toda tarefa precisa de modelo frontier.

## H3 — TaskContract

Contratos estruturados reduzem scope drift.

## H4 — Circuit Breaker

Loops ruins podem ser interrompidos cedo sem prejudicar significativamente execuções válidas.

## H5 — Verification DAG

Múltiplas evidências independentes detectam falhas melhor que um único LLM reviewer.

## H6 — Evidence Graph

Histórico de execução melhora investigação de regressões e futuras decisões.

---

# 39. Critérios de morte

A ideia deve ser falsificável.

## Context Compiler

Se não melhorar sucesso/custo:

- remover como diferencial.

## Model Router

Se economia for irrelevante:

- manter apenas como infraestrutura.

## Semantic Linter

Se falsos positivos forem altos:

- não transformar em gate.

## Jarvis Proativo

Se alertas forem ignorados:

- reduzir interrupções ou eliminar comportamento.

## Evidence Graph

Se os dados não produzirem decisões melhores:

- simplificar para audit log.

## Adaptive Controller

Se heurísticas simples forem suficientes:

- não introduzir ML prematuramente.

---

# 40. Riscos estratégicos

## Incumbentes

GitHub, OpenAI, Anthropic, Cursor e outros podem incorporar:

- memory;
- routing;
- review;
- policy;
- orchestration.

Portanto o moat não pode ser feature parity.

## Comoditização de modelos

Modelos ficarão:

- melhores;
- mais baratos;
- mais rápidos.

Economia de tokens isolada não é defensável.

## Vendor lock-in

Todos os componentes devem depender de abstrações.

## Segurança

O AECS concentra acesso a sistemas críticos.

## Telemetria incompleta

Relacionar mudança a bug posterior não significa necessariamente causalidade.

## Cold start

O Adaptive Controller inicialmente não terá dados.

---

# 41. Moat pretendido

O ativo defensável não será:

- UI;
- chat;
- prompt;
- extensão;
- modelo específico.

O moat potencial é:

# Engineering Execution Dataset

Dados longitudinais sobre:

```text
task type
repository characteristics
model
agent
context strategy
tools
budget
verification
human review
cost
retries
merge
rework
bugs
incidents
production outcome
```

Com volume suficiente, o sistema poderá aprender:

> qual estratégia produz a mudança correta pelo menor custo total para cada classe de tarefa.

---

# 42. Posicionamento futuro

Não vender:

> “Jarvis para programadores.”

Não vender:

> “IA que programa.”

Não vender:

> “economize tokens.”

Posicionamento possível:

> **Independent Engineering Intelligence for Agentic Software Development.**

Ou:

> **Control, verification and intelligence for AI-generated software.**

A experiência pode parecer Jarvis.

A categoria deve ser engenharia e controle.

---

# 43. Visão de longo prazo

Um engenheiro poderá dizer:

> “Implemente subscription billing.”

O AECS:

1. compreende intenção;
2. cria TaskContract;
3. identifica módulos afetados;
4. estima risco;
5. busca decisões e incidentes anteriores;
6. seleciona contexto;
7. escolhe modelo/agente;
8. define budget;
9. executa em sandbox;
10. monitora comportamento;
11. interrompe desvios;
12. executa Verification DAG;
13. gera evidências;
14. exige aprovação quando necessário;
15. acompanha merge;
16. correlaciona resultado de produção;
17. aprende com o resultado.

O humano permanece responsável por:

- intenção;
- arquitetura;
- decisões críticas;
- trade-offs;
- risco;
- aprovação.

Os agentes executam.

O AECS controla e mede.

---

# 44. Roadmap inicial

## Milestone 0 — Foundation

- criar repositório;
- criar solução .NET;
- ADR-001;
- modelos de domínio;
- PostgreSQL;
- Docker Compose;
- OpenTelemetry básico.

## Milestone 1 — TaskContract

- parser;
- validação;
- risk classification;
- budget;
- scope.

## Milestone 2 — Agent Runtime

- primeiro `IAgentAdapter`;
- execução Docker;
- captura stdout/stderr;
- timeout;
- token/cost tracking.

## Milestone 3 — Control Kernel

- scope enforcement;
- budget;
- retry;
- circuit breaker;
- policy decisions.

## Milestone 4 — Verification

- build verifier;
- unit test verifier;
- scope verifier;
- architecture verifier básico.

## Milestone 5 — Evidence

- Evidence model;
- persistence;
- execution trace;
- primeira visualização.

## Milestone 6 — Experiment Harness

- dataset de tarefas;
- repeated runs;
- model comparison;
- CPVC.

## Milestone 7 — Context Compiler

- Roslyn symbol graph;
- relevance selection;
- ContextPackage;
- A/B experiments.

## Milestone 8 — Adaptive Controller

- heuristics;
- historical statistics;
- posterior strategy selection.

## Milestone 9 — Jarvis UX

- CLI inteligente;
- VS Code client;
- explicações;
- alertas;
- human approvals.

---

# 45. Primeiro ADR recomendado

Criar:

```text
docs/adr/ADR-001-control-philosophy.md
```

Decisão:

> AECS utilizará modelos probabilísticos para descoberta e raciocínio, mas enforcement crítico deverá ser determinístico sempre que tecnicamente possível.

Esse ADR deve ser considerado fundacional.

---

# 46. Primeira definição de sucesso

O AECS v0.1 será considerado tecnicamente promissor se conseguir demonstrar:

1. execução controlada de um coding agent;
2. isolamento;
3. budget enforcement;
4. scope enforcement;
5. reconstrução completa da execução;
6. verificação automática;
7. comparação objetiva entre estratégias;
8. cálculo inicial de custo por mudança verificada.

Não é necessário:

- ter UI bonita;
- possuir multi-agent;
- ter voz;
- suportar todas as linguagens;
- possuir ML próprio.

---

# 47. Pergunta que deve orientar todas as decisões

Sempre perguntar:

> **Isso aumenta nossa capacidade de produzir, verificar ou aprender sobre mudanças de software com menor custo total e menor risco?**

Se não:

provavelmente não pertence ao núcleo do AECS.

---

# 48. Resumo executivo

A tese chegou ao seguinte ponto:

```text
AI Coding Agent
      ↓
produces change
      ↓
AECS
      ↓
controls execution
      ↓
verifies evidence
      ↓
measures total cost
      ↓
observes production outcome
      ↓
learns
      ↓
improves next execution
```

O “Jarvis” é a camada humana dessa máquina.

O verdadeiro sistema é:

# Agentic Engineering Control System

E sua métrica principal é:

# Cost per Verified Change

O objetivo final não é gerar mais código.

É:

> **permitir que humanos controlem sistemas que geram software em uma velocidade superior à capacidade humana de inspeção, preservando entendimento, segurança, arquitetura, qualidade e economia.**
