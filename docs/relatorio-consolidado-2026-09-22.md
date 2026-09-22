# AECS — Relatório consolidado de estado

**Data:** 2026-09-22 · **Branch:** `dev` @ `01777ff` · **CI:** verde (CI 5m22s + AECS Pipeline 21m49s)
**Escala:** 257 commits · 327 arquivos `.cs` · 56 contratos YAML · **591/591 testes** (458 unit + 133 integration; 7 PostgreSQL ignorados por ambiente)

Este relatório consolida (a) o que está **implementado e verificado** e (b) o que **ainda precisa ser implementado**, distinguido por: entregue · pendência declarada · fora de escopo desta fase (regra do projeto).

---

# Parte 1 — O que já está funcionando

## 1.1 Pipeline principal (staged execution) — VERIFICADO

Fluxo completo de ponta a ponta, com evidência para cada etapa:

| Etapa | Componente | Estado |
|---|---|---|
| Contrato | `TaskContractParser` (YAML v1, dual-property, 59 contratos rastreados) | ✅ |
| Risco | `RiskClassifier` R0–R4 (R4 infraestrutura) | ✅ |
| Baseline | `GitWorkspaceManager` — worktree staged, rejeita repositório sujo, baseline intocado atestado | ✅ |
| Snapshot | `RepositorySnapshotBuilder` (git tree canônico, exclusões) | ✅ |
| Symbol graph | `RoslynSymbolGraphBuilder` — **`loaded=True`** (14 projetos / ~10k nodes no repo AECS); limites fail-closed; diagnostic detalhado no codec | ✅ |
| Contexto | `RepositoryContextCompiler` — index regex + `graph-ranked`/`naive-path-order` + orçamento de tokens + seção `ACTIVE CONSTRAINTS` | ✅ |
| Agente | `OllamaAdapter` (local), `CloudAdapter` (OpenAI-compat), `MockAgentAdapter`, `FallbackAdapter` (local→cloud) | ✅ |
| Escrita | `FileApplicator` — `FILE:`, `### path`, `### FILE: path`, fence `// path.ext` (modelos fracos); rejeita traversal/`.git`/absolutos; validação por escopo no `ScopeVerifier` | ✅ |
| Controle | `ExecutionController` (modelo por risco: R0→1.5b, R1–R4→3b + cloud), `BudgetEnforcer`, `ControlKernel`, circuit breaker | ✅ |
| Verificação | Build · Tests · Scope · Budget · SecurityScan · AcceptanceCriteria · EB001–EB005 (Semantic Linter) · **ConstraintLedger** · suite matrix de testes | ✅ |
| Decisão | `DecisionEngine` — required verifiers; ledger obrigatório; pending → `HumanReviewRequired`; aprovação humana | ✅ |
| Evidência | Envelope assinado RSA-PSS/SHA-256 + cadeia (promotion/replay), validações fail-closed (symbol graph, trajectory, constraint set com recomputação de hash) | ✅ |
| Sandbox | Docker staged (network/capabilities por fase) + `runtime: host`; `--allow-host-execution` | ✅ |
| Promoção/Replay | `CandidatePromotionService` (diff autenticado), `ExecutionReplayService` (gates reproduzíveis, inclui ledger) | ✅ |
| Adaptive | `AdaptiveController` shadow + offline gate (histórico ≥5 execuções) | ✅ |
| CLI/Jarvis | `aecs run/experiment/doctor/evidence/promote/replay/…`, REPL Jarvis, backend VS Code | ✅ |

## 1.2 Constraint Continuity (plano P0–P3) — VERIFICADO

| Componente | Estado |
|---|---|
| P0 auditoria de lacunas | ✅ `docs/architecture.md` |
| `ConstraintRecord`/`ConstraintSetRef`/conflitos + selo SHA-256 | ✅ |
| `ConstraintLedgerService` — ingest, supersede (re-sellado), revoke, conflitos, set-hash determinístico | ✅ |
| `ConstraintLedgerProjector` — contrato → records determinísticos (IDs de SHA-256, timestamps fixos) | ✅ |
| `ConstraintLedgerVerifier` — gate obrigatório: determinística violada ⇒ **Rejected**; manual/assistida ⇒ **PendingReview → HumanReviewRequired**; conflito ⇒ Rejected | ✅ |
| Context Compiler injeta `## ACTIVE CONSTRAINTS` (isento do teto de budget) | ✅ |
| `ConstraintSet` + `ConstraintRecords` no envelope + validação fail-closed (hash recomputado) | ✅ |
| Revalidação no `PublishAsync` (set mudou ⇒ aborta) | ✅ |
| Persistência multi-turno `JsonConstraintLedgerStore` — só origens `HumanOverride`/`HistoricalDecision` | ✅ |
| **HMAC-SHA256** no store (chave no evidence key dir, envelope autenticado, fail-closed) | ✅ |
| **Rotação** da chave HMAC via `aecs evidence-key rotate` (valida MAC antigo antes, `.bak`, rollback) | ✅ |
| **TrajectoryEvidence** (§4.3.7) — `Trajectory.NoNetwork`: docker sem rede ⇒ Satisfied; com rede ⇒ Violated; host ⇒ Pending | ✅ |
| Corpus §4.4 **R1–R8** + `Coverage_AllPlanCasesAreCovered` | ✅ (16 testes multi-turn) |

## 1.3 Modular Harness (plano P4–P5) — VERIFICADO

| Componente | Estado |
|---|---|
| `HarnessManifest` `aecs.harness/v1` — hash canônico, allowlist de módulos/estratégias/parâmetros | ✅ |
| Gates imutáveis `aecs.immutable-gates/v1` (inclui `ConstraintLedger`); `tool-use.policyRef` fixo; auto-modificação bloqueada | ✅ |
| Dataset fatorial `aecs.experiment-dataset/v5` (`factorial-ledger-context-2x2`) — braços A–D validados pelo loader | ✅ |
| Fator `constraintLedgerEnabled` por variante (pipeline on/off por braço) | ✅ |
| Relatório com `armSummaries` (CRR, cobertura), 5 contrates + **interação**, `falseBlockCandidates`, `Limitations` | ✅ |
| Fixture CI (mock, 8 runs) + step no `ci.yml` | ✅ |

## 1.4 Observabilidade (OpenTelemetry básico) — VERIFICADO

- `ActivitySource("AECS")` (BCL-only no Application) · spans `aecs.execution/symbol-graph/agent/verify` · 10 eventos do vocabulário §28 (`task.created` … `patch.rejected`)
- Export **OTLP opcional** via `OTEL_EXPORTER_OTLP_ENDPOINT` (padrão OTEL); sem endpoint = zero rede, listener-friendly
- Testes: unit (fonte + vocabulário) + integração (pipeline inteiro emite spans/eventos)

## 1.5 Experimentos executados (protocolo fatorial, provider real)

| Data | Corpus | Provider | Runs | VCC | CPVC | Papel |
|---|---|---|---|---|---|---|
| 09-20 | 2 tarefas easy | local 3b | 24 | 67% | $0,0002 | primeiro real local |
| 09-21 | 2 tarefas easy | cloud mimo-v2.5 | 24 | **100%** | $0,0021 | **teto** |
| 09-21 | 20 tarefas RealProject (piso) | cloud mimo-v2.5 | 80 | **0%** | N/D | **piso** — ledger CRR 0,67–0,77 truthful |
| 09-22 | 4 calibradas reps=1 | cloud | 16 | 25% | N/D | bracket inicial |
| 09-22 | **4 calibradas reps=5** | cloud | **80** | **42,5%** | **$0,0208 [0,0163–0,0285]** | **medição com incerteza** |

Achados exploratórios do n=20 (documentados com limitações): seção de obrigações no prompt correlaciona com ΔVCC **−12** (perturbação de prompt — violações 100% verdadeiras, `falseBlocks=0`); graph-ranked −6 neste corpus; interação +6.

**Cloud funcional:** MiMo Capability API (`mimo serve --port 4096` no diretório do repo + token `llm-server`), doctor `cloud-fallback: ready`, run real end-to-end com EB001–005 + ConstraintLedger disparando.

## 1.6 Qualidade de engenharia — VERIFICADO

- **591/591 testes** · CI (`build-test-smoke` + `real-world-e2e` + dois smokes de experimento) e AECS Pipeline verdes no último push
- 29 tasks de sessão concluídas (T48–T99) nesta arcada; zero árvore suja, `origin/dev` sincronizado
- Correções de infra desta arcada: ProgramFiles HKLM restaurado, FileApplicator regex/`//path`, symbol graph containing-id, baseline tests não-bloqueantes, timeout E2E 15min, MoP de modelo 1.5b/3b

---

# Parte 2 — O que ainda precisa ser implementado

## 2.1 Do plano — ainda não entregue

| Item | Origem | Estado | Esforço estimado |
|---|---|---|---|
| **P6 — propostas automáticas de evolução do harness** | Plano §7 | **Bloqueado por design** — exige P0–P5 consolidados + avaliação independente + aprovação humana explícita + rollback; autoaprovação proibida | Grande (quando liberado) |
| **Runner `Assisted`** | Pendência §4 (Constraint Continuity) | `Assisted`/`Manual` sem verificador semi-automatizado ⇒ sempre `PendingReview` (correto, mas nenhum caminho para evidência assistida gerada) | Médio: um verificador que gera evidência para revisão humana sem nunca auto-aprovar |
| **Corpus §6 mínimo: 20–30 tarefas distintas + 5–10 turnos** | Plano §6 / §7 P3 | Corpus calibrado tem **4 tarefas × 5 reps** (diversidade intra-tarefa ok, inter-tarefa não); turnos = apenas testes unitários R1–R8 — **não há execução runtime multi-sessão medida** | Médio–alto: autoria de ~16+ tarefas calibradas e harness de sessões multi-turno |
| **Métricas CUD e H2–H6** | Fundação §17/§38 | Só H1 (Context Compiler) foi medido; CUD (`ΔP(VCC)/tokens`), H2 (routing), H3–H6 não avaliados | Médio, após corpus estável |

## 2.2 Pendências declaradas (verificação incompleta / não verificado)

| Item | Estado atual | O que falta |
|---|---|---|
| **Diversidade de provider/temperatura** | Temperatura default não fixada em todas as variantes; provider único (mimo) nas medições | Fixar `temperature`/`seed` por variante; repetir bracket em 2º provider |
| **A/B da seção `ACTIVE CONSTRAINTS`** | Achado n=20: −12 VCC correlacionado ao texto de obrigações | Experimento dedicado: posição (topo vs fim), tamanho, ou ocultar em runs de baixo risco |
| **Trajetória de processo — só rede/runtime** | `Trajectory.NoNetwork` cobre rede; comandos ficam em `ExecutionCommandEvidence` | Mapear mais invariantes (ex.: executáveis proibidos) com verificadores nomeados `Trajectory.*` |
| **Evidence Graph longitudinal** | `EvidenceGraphProjection` + docs existem | Consultas históricas ricas / grafo por corridas não são interface de produto; parcial |
| **PostgreSQL evidence store** | Implementado; 7 testes **skipped** sem PG no ambiente local | Subir PG (Docker Compose) e rodar os 7 testes — CI/local com serviço |
| **OpenTelemetry — só traces** | Spans + eventos §28 + OTLP opcional | Métricas/logs OTEL; spans de HTTP do Ollama/CloudAdapter; dashboards |
| **Token de capability com vida útil** | TTL 30d deslizante, renew manual; `mimo serve` precisa ficar vivo (porta 4096) | Documentar/auto-renovar; automação de restart do serve |
| **Retry de formato do agente** | `NonEmptyChange` falha e **não** re-tenta o agente (retry é só de falha do agente) | Decidir: tratar "resposta sem arquivo parseável" como retryable no coordinator |
| **Modelo local 7b/14b removidos** | Roteamento usa 1.5b/3b | Re-pull opcional do 7b se quiser tier local de qualidade |
| **Rotação da chave RSA+HMAC** | Manual (`evidence-key rotate`) | Agenda/alerta de expiração (menor prioridade) |

## 2.3 Validação científica — lacunas explícitas

1. **Bracket fechado mas com n pequeno por tarefa** — 4 tarefas × 5 reps; §6 exige 20–30 tarefas distintas para conclusão generalizável. Números atuais são **protocolo validado**, não eficácia comprovada (docs de validação declaram isso).
2. **Nenhum experimento multi-turno em runtime** — R1–R8 são unit; falta corpus de sessões5–10 turnos com persistência de ledger medida de verdade.
3. **Ledger-on com VCC menor (−12)** — se confirmar em amostra maior, exige iteração de design da seção de contexto (o próprio AECS precisa evoluir o harness — ironicamente o tipo de coisa que P6 faria, mas P6 continua bloqueado por design até evidência + aprovação humana).

## 2.4 Fora de escopo desta fase (regra do projeto — **não** é dívida)

Fundação §31/§35 e regras de desenvolvimento excluem explicitamente: **frontend/Angular, UX Jarvis rica, extensão VS Code como produto** (backend já existe), **multi-agent, RL/contextual bandits, K8s, Neo4j, Kafka, microservices, multi-região, voz, OPA/Rego como policy engine, pgvector/RAG** (RAG nunca foi planejado — retrieval é o Context Compiler), **Engineering Entropy**, **Change Attestation** e demais itens pós-MVP da §30.

---

# Parte 3 — Números-resumo

```text
Testes                 591/591 (0 falhas; 7 skipped PG)
CI (último push)       CI success 5m22s · AECS Pipeline success 21m49s
Entrega de planos      Milestones 0–9: 10/10 · Plano P0–P5: 5/5 · P6: bloqueado por design
Experimentos cloud     teto 100% · bracket 42,5% (CPVC $0,0208 [0,0163–0,0285]) · piso 0%
Ledger (bracket n=20)  falseBlocks=0 · cobertura 1,0 · CRR 0,69–0,81 · violações 100% truthful
Custo acumulado (runs reais) ≈ $1,4 adapter-estimate (easy $0,05 + corpus $0,54 + cal1 $0,11 + cal5 $0,71)
```

**Conclusão:** o sistema de controle (discovery probabilístico + enforcement determinístico) está **operante e medido de ponta a ponta**. O que separa o estado atual de uma tese experimental madura é **volume e diversidade de dados** (tarefas, turnos, providers), não capacidade de software. As únicas features de código ainda em aberto dentro do escopo desta fase são o **runner Assisted** e as **extensões de trajetória** — o resto é pesquisa/calibração ou bloqueio deliberado (P6).

---

*Gerado em 2026-09-22 a partir do estado `dev@01777ff`. Fontes: `docs/architecture.md`, `AECS_PLANO_IMPLEMENTACAO_CONSTRAINT_LEDGER_MODULAR_HARNESS.md`, `docs/validation/2026-09-2*.md`, suíte local e GitHub Actions.*
