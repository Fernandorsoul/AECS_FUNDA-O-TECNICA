# Validação — Corpus calibrado n=20 pares (reps=5): contrastes com incerteza

**Data:** 2026-09-22
**Dataset:** `tests/fixtures/experiment-factorial-calibrated/dataset.cloud.json` (`repetitions: 5`)
**Design:** `factorial-ledger-context-2x2`
**Provider:** Cloud — `xiaomi/mimo-v2.5` via MiMo Capability API
**Amostra:** 4 tarefas × 4 braços × 5 repetições = **80 runs** · **20 pares por contraste**
**Duração / custo:** 3.942s (~66 min) · **$0,7086** (custo **totalmente reconciliado**, missingCost=0)

## Resultado principal

- **First-pass / VCC: 42,5% (34/80)** — dentro de (0,1), consolidando o bracket
- **CPVC: $0,0208** (95% CI **[$0,0163, $0,0285]**)
- `falseBlockCandidates = 0` · cobertura de verificação = **1,0** em todos os braços com ledger

## Braços (20 runs cada)

| Braço | VCC | First-pass | CRR | viol | Tokens | Custo | CPVC* |
|---|---|---|---|---|---|---|---|
| A (naive, ledger off) | **16/20** | 80% | N/D | 0 | 62,1k | $0,1507 | **$0,0094** |
| B (naive, ledger on) | 4/20 | 20% | **0,81** | 19 | 62,8k | $0,1557 | $0,0389 |
| C (graph, ledger off) | 10/20 | 50% | N/D | 0 | 77,4k | $0,2002 | $0,0200 |
| D (graph, ledger on) | 4/20 | 20% | **0,69** | 31 | 78,9k | $0,2021 | $0,0505 |

\* CPVC por braço = custo ÷ VCC (derivado; o agregado oficial é $0,0208).

## Contrastes pareados (20 pares)

| Contraste | ΔVCC | Δtokens | Δcusto |
|---|---|---|---|
| ledger-effect-baseline (B−A) | **−12** | +701 | +$0,005 |
| ledger-effect-graph-ranked (D−C) | **−6** | +1.562 | +$0,002 |
| harness-effect-ledger-off (C−A) | **−6** | +15.249 | +$0,050 |
| harness-effect-ledger-on (D−B) | 0 | +16.110 | +$0,046 |
| **interaction (D−C)−(B−A)** | **+6** | +861 | −$0,003 |

## Leitura (exploratória, n=20 pares — não é claim de eficácia)

1. **Ledger não é "grátis" no braço atual:** as execuções com a seção `ACTIVE CONSTRAINTS` no prompt tiveram VCC **80%→20%** (A→B) com **todas as violações verdadeiras** (19/31 assessments batem com falhas reais de Build/Tests; `falseBlocks=0` pela definição). O mecanismo mais provável é **perturbação de prompt** — o texto de obrigações altera a geração do modelo (tokens totais praticamente iguais: +701 — não é estouro de orçamento de contexto). Isso é um achado de design para o AECS: visibilidade de restrições no contexto pode reduzir o sucesso do modelo e merece A/B próprio (posição/tamanho da seção).
2. **graph-ranked pior que naive neste corpus** (50% vs 80% com ledger off, Δ−6): mais contexto (+15k tokens) correlaciona com **pior** VCC — contrário à direção do H1 original (outro corpus, outro modelo). Exploratório; não reabre o H1.
3. **Interação +6** — os fatores não são aditivos; atribuição a um único fator estaria errada (§6).
4. Custo por VCC: A é ~5× mais barato que D ($0,0094 vs $0,0505).

## Bracket consolidado (mesmo provider cloud)

| Corpus | VCC | Posição |
|---|---|---|
| 2 tarefas easy | 100% | teto |
| **4 calibradas reps=1** | **25% (4/16)** | (0,1) inicial |
| **4 calibradas reps=5** | **42,5% (34/80)** | **(0,1) — CPVC com CI** |
| 20 duras | 0% | piso |

## Limitações (obrigatórias)

- 4 tarefas × 5 reps — variância **intra-tarefa**, não diversidade de tarefas; §6 pede 20–30 tarefas distintas para conclusão externa.
- Provider único; temperatura default (não fixada) — variabilidade parcialmente não controlada.
- CPVC por braço derivado, não reconciliado individualmente pelo cost-efficiency analyzer.
- 1 timeout transitório de API em runs anteriores desta série (não neste lote — todos completos).
- Custo = estimativa adapter ($0,7086).

## Reprodução

```bash
cp -R tests/fixtures/experiment-factorial-calibrated <tmp>
git -C <tmp>/repository init --initial-branch main && git -C <tmp>/repository add -A \
  && git -C <tmp>/repository commit -m baseline
aecs experiment --dataset <tmp>/dataset.cloud.json --output <tmp>/out \
  --include-real-providers --allow-host-execution --resume \
  --evidence-root <tmp>/ev --key-directory <tmp>/keys
```

Datasets irmãos: `dataset.json` (mock, reps=1), `dataset.local.json` (3b, reps=1).
Anteriores: `2026-09-21-factorial-corpus-cloud.md` (piso), `2026-09-21-factorial-cloud-provider.md` (teto), `2026-09-20-factorial-local-provider.md`.
