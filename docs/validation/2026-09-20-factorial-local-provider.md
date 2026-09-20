# Validação — Protocolo fatorial Ledger × Context com provider real local

**Data:** 2026-09-20
**Dataset:** `tests/fixtures/experiment-factorial/dataset.local.json`
**Design:** `factorial-ledger-context-2x2` (`aecs.experiment-dataset/v5`)
**Provider:** Ollama local `qwen2.5-coder:3b` (seed 100, `--include-real-providers`)
**Amostra:** 2 tarefas × 4 braços × 3 repetições = **24 runs** (6 pares por contraste)
**Duração / custo:** 133,4s · $0,0025 (estimativa adapter) · first-pass **67% (16/24)** · **CPVC $0,0002** (95% CI [$0,0001, $0,0003])

## Braços

| Braço | Ledger | Contexto | VCC | First-pass | CRR | Cobertura |
|---|---|---|---|---|---|---|
| A (referência) | off | naive-path-order | **6/6** | 100% | N/D | N/D |
| B | on | naive-path-order | 4/6 | 67% | **1.0** | **1.0** |
| C | off | graph-ranked | 2/6 | 33% | N/D | N/D |
| D | on | graph-ranked | 4/6 | 67% | **1.0** | **1.0** |

- **CRR = 1.0 em todos os braços com ledger** (24 assessments `satisfied`, 0 `violated`, 0 `pending`) — nenhuma rejeição veio de violação de restrição.
- Rejeições = `NonEmptyChange` (o `qwen2.5-coder:3b` às vezes não emite o formato `FILE:`/`// path` — flake estocástico de formato, não violação de política).

## Contrastes pareados (ΔVCC sobre 6 pares)

| Contraste | ΔVCC | Δfirst-pass | Δtokens |
|---|---|---|---|
| ledger-effect-baseline (B−A) | −2 | −2 | +496 |
| ledger-effect-graph-ranked (D−C) | **+2** | +2 | +504 |
| harness-effect-ledger-off (C−A) | **−4** | −4 | +20 |
| harness-effect-ledger-on (D−B) | 0 | 0 | +28 |
| **interaction (D−C)−(B−A)** | **+4** | +4 | +8 |

- `falseBlockCandidates = 2` (definição: ledger-on rejeitou com 0 violações onde ledger-off verificou) — mecanismo: flake de formato do modelo, **não** bloqueio do gate (CRR=1.0).
- A interação **+4** impede atribuir o efeito conjunto a um único fator (plano §6).

## Calibração executada durante a validação

1. `wall_clock_seconds` 30 → **180** (orçamento estourava antes do fim da geração).
2. Objetivos dos contratos tornados autocontidos (modelo real sem código existente não tinha o que escrever).
3. Prompt reforçado (`FILE:` obrigatório, exemplo `.txt`).
4. `FileApplicator` passou a aceitar `// path.ext` como primeira linha do fence (formato que o 3b emite consistentemente); validação de segurança (`..`, `.git`, absolutos) inalterada e testada.

## Limitações (obrigatórias)

- **Validação de protocolo, não de eficácia** — provider local, 2 tarefas, 6 pares; incerteza grande.
- Flake estocástico de formato do 3b domina as rejeições; CRR puro não deve ser lido como "ganho do ledger".
- Braço **cloud bloqueado**: key Groq em `.env` retorna 401 (rotacionada) — repetir com cloud é o próximo passo para conclusão com incerteza real.
- Custo local é estimativa adapter ($0,0025), não faturamento.
- Execução anterior com 1 repetição (8 runs, 50% first-pass) mostrou split A/C vs B/D que **não se repetiu** em n=3 — evidência de estocasticidade, não de efeito sistemático da seção de obrigações no prompt.

## Reprodução

```bash
# fixture copiada para temp + git init (baseline HEAD)
aecs experiment --dataset <tmp>/dataset.local.json --output <tmp>/out \
  --include-real-providers --allow-host-execution \
  --evidence-root <tmp>/ev --key-directory <tmp>/keys
```

Dataset mock para CI: `dataset.json` (1 repetição, 8 runs) — inalterado.
