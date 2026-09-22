# Validação — Corpus calibrado: VCC dentro de (0,1)

**Data:** 2026-09-22
**Dataset:** `tests/fixtures/experiment-factorial-calibrated/dataset.cloud.json`
**Design:** `factorial-ledger-context-2x2`
**Provider:** Cloud — `xiaomi/mimo-v2.5` via MiMo Capability API
**Amostra:** 4 tarefas × 4 braços × 1 repetição = **16 runs** (4 pares por contraste)
**Duração / custo:** 1.934s (~32 min) · $0,1122 (estimativa adapter; 4 runs sem custo ⇒ CPVC **N/D**)

## Resultado — bracket alcançado

**First-pass / VCC: 25% (4/16)** — estritamente dentro de **(0,1)**, entre o teto (100% no corpus easy) e o piso (0% no corpus de 20 tarefas):

| Braço | VCC | First-pass | CRR | viol | Tokens |
|---|---|---|---|---|---|
| A (naive, ledger off) | **3/4** | 75% | N/D | 0 | 11.344 |
| B (naive, ledger on) | 0/4 | 0% | **0,70** | 6 | 12.140 |
| C (graph, ledger off) | **1/4** | 25% | N/D | 0 | 9.903 |
| D (graph, ledger on) | 0/4 | 0% | **0,70** | 6 | 12.518 |

Baseline do fixture: **3/22 testes falhando** (Customers: Equals + Mapper.ToDto/ToEntity), 19 verdes — o modelo precisa reparar 2 arquivos + tarefa específica.

## Integridade do ledger — sem falsos positivos

Inspecionei os **8 runs dos braços B/D**: toda violação do ledger corresponde a uma falha **real** do gate nomeado:

| Runs | Gate real | Assessment do ledger |
|---|---|---|
| 5 | `Tests: Fail` | `verification.tests` violated |
| 3 | `Build: Fail` (ou Build Skip por NonEmptyChange) | `verification.build` violated |

- **0 falsos positivos** · `falseBlockCandidates = 0` · cobertura de verificação = 1,0
- Rejeições adicionais em A/C: 2× `NonEmptyChange` (modelo não emitiu arquivo)

## Contrastes (4 pares) — fatores agora diferenciam VCC

| Contraste | ΔVCC | Δtokens |
|---|---|---|
| ledger-effect-baseline (B−A) | **−3** | +796 |
| ledger-effect-graph-ranked (D−C) | **−1** | +2.615 |
| harness-effect-ledger-off (C−A) | **−2** | −1.441 |
| harness-effect-ledger-on (D−B) | 0 | +378 |
| **interaction (D−C)−(B−A)** | **+2** | +1.819 |

Com VCC estritamente em (0,1), os contrastes deixaram de ser trivialmente 0 — a interação **+2** é observável. Com n=4 pares por contraste isto é **sinal exploratório**, não conclusão (como já declarado nas limitações).

## Arco completo dos três corpora (mesmo protocolo, mesmo provider cloud)

| Corpus | VCC | Posição |
|---|---|---|
| 2 tarefas `result.txt` | **100%** (24/24) | teto |
| **4 tarefas calibradas** | **25% (4/16)** | **(0,1) — domínio mensurável** |
| 20 tarefas RealProject | **0%** (0/80) | piso |

## Limitações (obrigatórias)

- n=4 pares por contraste — exploratório; contrastes de VCC não sustentam inferência estatística.
- CPVC **N/D** (4 runs sem custo registrado; denominador zero ⇒ nunca reportar 0).
- Braços com ledger tiveram mais falhas de execução (prompt de obrigações + estocasticidade do provider) com n pequeno — efeito não separável nesta amostra; exige mais repetições.
- 1 timeout transitório da API cloud (`Cloud API call timed out`).
- Custo = estimativa adapter ($0,1122), não faturamento.

## Reprodução

```bash
cp -R tests/fixtures/experiment-factorial-calibrated <tmp>
git -C <tmp>/repository init --initial-branch main && git -C <tmp>/repository add -A \
  && git -C <tmp>/repository commit -m baseline
aecs experiment --dataset <tmp>/dataset.cloud.json --output <tmp>/out \
  --include-real-providers --allow-host-execution --resume \
  --evidence-root <tmp>/ev --key-directory <tmp>/keys
```

Fixture baseline check (apenas Customers quebrado): `dotnet test` no `repository/` ⇒ 19 pass / 3 fail.
