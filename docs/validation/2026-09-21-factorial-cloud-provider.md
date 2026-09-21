# Validação — Protocolo fatorial Ledger × Context com provider cloud (MiMo Capability API)

**Data:** 2026-09-21
**Dataset:** `tests/fixtures/experiment-factorial/dataset.cloud.json`
**Design:** `factorial-ledger-context-2x2` (`aecs.experiment-dataset/v5`)
**Provider:** Cloud via MiMo Capability API (`xiaomi/mimo-v2.5` em `127.0.0.1:4096/v1`, token escopado `llm-server`)
**Amostra:** 2 tarefas × 4 braços × 3 repetições = **24 runs** (6 pares por contraste)
**Duração / custo:** 677s · **$0,0501** (estimativa adapter) · first-pass **100% (24/24)** · **CPVC $0,0021** (95% CI [$0,0016, $0,0026])

## Braços

| Braço | Ledger | Contexto | VCC | First-pass | CRR | Cobertura | Tokens |
|---|---|---|---|---|---|---|---|
| A (referência) | off | naive-path-order | **6/6** | 100% | N/D | N/D | 4520 |
| B | on | naive-path-order | **6/6** | 100% | **1.0** | **1.0** | 5485 |
| C | off | graph-ranked | **6/6** | 100% | N/D | N/D | 5935 |
| D | on | graph-ranked | **6/6** | 100% | **1.0** | **1.0** | 7163 |

- **`falseBlockCandidates = 0`** — nenhuma rejeição em braço com ledger; CRR=1.0 (24 assessments satisfied, 0 violações, 0 pending) em B e D.

## Contrastes pareados (Δ sobre 6 pares)

| Contraste | ΔVCC | Δfirst-pass | Δtokens | Δcusto |
|---|---|---|---|---|
| ledger-effect-baseline (B−A) | 0 | 0 | +965 | +$0,0019 |
| ledger-effect-graph-ranked (D−C) | 0 | 0 | +1228 | +$0,0027 |
| harness-effect-ledger-off (C−A) | 0 | 0 | +1415 | +$0,0042 |
| harness-effect-ledger-on (D−B) | 0 | 0 | +1678 | +$0,0050 |
| **interaction (D−C)−(B−A)** | **0** | **0** | **+263** | +$0,0008 |

- **Efeito teto em VCC:** a tarefa é simples demais para `mimo-v2.5` — todos os braços 100%, contrasts de VCC zerados por construção. O sinal observável é **custo/tokens**: ledger adiciona ~1k tokens de obrigações no prompt; `graph-ranked` adiciona ~1,4–1,7k tokens de contexto; interação +263 tokens.
- Com VCC no teto, **CPVC é a métrica que diferencia** ($0,0021 global; naive $0,0017 vs graph $0,0025 por braço de strategy).

## Comparação com o mesmo protocolo local (2026-09-20, `qwen2.5-coder:3b`)

| Métrica | Local 3b | Cloud mimo-v2.5 |
|---|---|---|
| First-pass | 67% (16/24) | **100% (24/24)** |
| CPVC | $0,0002 | $0,0021 |
| falseBlockCandidates | 2 (flake de formato) | **0** |
| CRR (B/D) | 1.0 | 1.0 |
| Duração | 133s | 677s |

- O ledger **não rejeitou nenhuma execução correta** em nenhum provider (CRR=1.0 nos dois) — coerência entre braços.
- O cloud elimina o flake de formato do 3b (causa das falsas rejeições locais).

## Limitações (obrigatórias)

- **2 tarefas × 6 pares** — amostra pequena; VCC no teto impede inferência de ganho de qualidade (só custo/tokens).
- Custo = estimativa adapter da Capability API, não faturamento.
- Base URL do loopback **muda se o `mimo serve` reiniciar** (porta fixa 4096 recomendada); token com TTL deslizante de 30d — ver renew em `aecs evidence-key`/`mimo llm-server issue`.
- Braço cloud depende de `mimo serve` vivo no diretório do repo; sem servidor, runs falham fechados (correto).

## Reprodução

```bash
# prerequisite: mimo serve --port 4096 launched from the AECS repo directory
#               + OPENAI_* in .env pointed at http://127.0.0.1:4096/v1 / xiaomi/mimo-v2.5
aecs experiment --dataset <tmp>/dataset.cloud.json --output <tmp>/out \
  --include-real-providers --allow-host-execution \
  --evidence-root <tmp>/ev --key-directory <tmp>/keys
```

Datasets irmãos: `dataset.json` (mock, CI smoke), `dataset.local.json` (Ollama 3b, repetições=3).
