# Validação — Corpus fatorial 20 tarefas com provider cloud (piso, não teto)

**Data:** 2026-09-21
**Dataset:** `tests/fixtures/experiment-factorial-corpus/dataset.cloud.json`
**Design:** `factorial-ledger-context-2x2` (`aecs.experiment-dataset/v5`)
**Provider:** Cloud — `xiaomi/mimo-v2.5` via MiMo Capability API
**Amostra:** 20 tarefas × 4 braços × 1 repetição = **80 runs** (20 pares por contraste)
**Duração / custo:** 5.316s (~89 min) · **$0,5392** (estimativa adapter; 2 runs sem custo ⇒ CPVC = N/D)

## Resultado principal

**First-pass / VCC: 0% (0/80)** — nenhum braço verificou nenhuma tarefa.

O corpus endurecido (baseline RealProject com **6 testes quebrados** + `build`/`unit_tests` required) **escapou do efeito teto** do corpus de 2 tarefas (que dava 100% com o mesmo provider): o modelo não consegue, em uma passada, reparar os 6 bugs base **e** completar a mudança da tarefa (média de 1,4 arquivo alterado por run vs. múltiplos arquivos necessários).

## Motivos de rejeição (80 runs)

| Motivo | Runs | Braço |
|---|---|---|
| `ConstraintLedger: verification.tests violada` (Tests Fail) | 40 | B/D (ledger on) |
| `Tests: Fail` direto | 33 | A/C (ledger off) |
| `Build: Fail` | 4 | — |
| `NonEmptyChange: Fail` | 3 | — |

**100% das rejeições se originam no gate de testes** (ou build). O ledger nos braços B/D reporta exatamente a mesma causa raiz — `sat`/`viol` por braço:

| Braço | VCC | CRR | sat | viol |
|---|---|---|---|---|
| A (naive, ledger off) | 0/20 | N/D | 0 | 0 |
| B (naive, ledger on) | 0/20 | **0.77** | 77 | 23 |
| C (graph, ledger off) | 0/20 | N/D | 0 | 0 |
| D (graph, ledger on) | 0/20 | **0.67** | 67 | 33 |

- `falseBlockCandidates = 0` (A/C nunca verificaram — não há falso bloqueio a contar)
- Cobertura de verificação = **1.0** em B/D (todas as restrições determinísticas avaliadas)
- Nenhum `pending` — ledger em si operou corretamente

## Contrastes (20 pares)

| Contraste | ΔVCC | Δtokens |
|---|---|---|
| ledger-effect-baseline (B−A) | 0 | +10.939 |
| ledger-effect-graph-ranked (D−C) | 0 | +7.809 |
| harness-effect-ledger-off (C−A) | 0 | +480 |
| harness-effect-ledger-on (D−B) | 0 | −2.650 |
| **interaction** | **0** | −3.130 |

**Leitura:** nenhum dos dois fatores (Ledger, estratégia de contexto) é uma intervenção de *conserto de código* — com VCC no piso ambos os fatores diferenciam apenas **custo de contexto** (ledger +~8–11k tokens; graf-ranked economiza ~2,6k nos braços com ledger por tarefas falhas curtas). O efeito teto e o piso agora **delimitam** o domínio de utilidade: os fatores só podem ser medidos onde VCC fica estritamente entre 0 e 1.

## Comparação entre os três corpora (mesmo protocolo)

| Corpus | Provider | VCC | CPVC | Sinal |
|---|---|---|---|---|
| 2 tarefas `result.txt` | mock | 100% (8/8) | N/D | plumbing |
| 2 tarefas `result.txt` | local 3b | 67% (16/24) | $0,0002 | flake de formato |
| 2 tarefas `result.txt` | cloud mimo | 100% (24/24) | $0,0021 | **teto** |
| **20 tarefas RealProject** | **cloud mimo** | **0% (0/80)** | **N/D** | **piso** |

## Limitações (obrigatórias)

- **Piso de dificuldade:** tarefas exigem reparo completo da suíte (6 bugs base) numa passada; retries de verificação não existem por design (retry é do agente, não do gate).
- Custo = estimativa adapter ($0,5392); 2 runs sem custo registrado ⇒ CPVC **N/D** (nunca 0).
- `repetitions: 1` — sem variância intra-tarefa.
- Resultado **não** implica que Ledger/Contexto sejam inúteis — implica que este corpus está fora da faixa em que os fatores podem diferenciar VCC.
- Próximo passo metodológico: calibrar dificuldade (fixar só parte dos bugs base, ou retries de agente, ou tarefas que tocam 1 módulo com testes verdes noutros) para VCC ∈ (0,1).

## Reprodução

```bash
cp -R tests/fixtures/experiment-factorial-corpus <tmp>
git -C <tmp>/repository init --initial-branch main && git -C <tmp>/repository add -A \
  && git -C <tmp>/repository commit -m baseline
aecs experiment --dataset <tmp>/dataset.cloud.json --output <tmp>/out \
  --include-real-providers --allow-host-execution --resume \
  --evidence-root <tmp>/ev --key-directory <tmp>/keys
```

Smoke de plumbing: `dataset.smoke.json` (mock, 8 runs) — ver README do fixture.
