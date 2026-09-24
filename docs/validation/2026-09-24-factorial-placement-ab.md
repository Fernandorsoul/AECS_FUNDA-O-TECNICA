# Validação — A/B: posicionamento da seção de obrigações (top vs bottom)

**Data:** 2026-09-24
**Design:** corpus calibrado (`experiment-factorial-calibrated`), fatorial 2×2, `repetitions: 5`
**Fator novo:** `constraintSectionPlacement` ∈ {top (default), bottom} — única diferença entre as duas execuções; obrigações **sempre presentes** (plano §4.3.3)
**Provider:** Cloud — `xiaomi/mimo-v2.5` · **80 runs por braço** · datasets `dataset.cloud.json` (top) vs `dataset.cloud-bottom.json`

## Resultado principal

| Métrica | **top** | **bottom** | Δ |
|---|---|---|---|
| First-pass / VCC | 42,5% (34/80) | **59,0% (47/80)** | **+16,5 pp** |
| CPVC | $0,0208 [0,0163–0,0285] | **$0,0139** | **−33%** |
| Custo total (adapter) | $0,7086 | $0,6541 | −$0,05 |
| Duração | 66 min | 52 min | −21% |

## Braços (20 pares cada)

| Braço | top VCC | **bottom VCC** | Δ | bottom CRR | bottom viol | bottom CPVC* |
|---|---|---|---|---|---|---|
| A (naive, ledger off) | 16/20 | **17/20** | +1 | — | 0 | $0,0078 |
| B (naive, ledger on) | 4/20 | **15/20** | **+11** | **0,92** | 8 | $0,0089 |
| C (graph, off) | 10/20 | 8/20 | −2 | — | 0 | $0,0243 |
| D (graph, on) | 4/20 | **7/20** | **+3** | **0,80** | 20 | $0,0275 |

\* CPVC por braço = custo ÷ VCC.

## Contrastes (20 pares) — top vs bottom

| Contraste | top ΔVCC | **bottom ΔVCC** |
|---|---|---|
| ledger-effect-baseline (B−A) | **−12** | **−2** |
| ledger-effect-graph-ranked (D−C) | −6 | **−1** |
| harness-off (C−A) | −6 | −9 |
| harness-on (D−B) | 0 | −8 |
| interaction | +6 | +1 |

`falseBlocks = 0` nas duas execuções · cobertura de verificação = 1,0 · todas as violações do ledger continuam batendo com falhas reais de Build/Tests.

## Conclusão do A/B (mecanismo do achado n=20)

O custo de −12 VCC do braço com ledger na execução top **quase desaparece no bottom (−2)** — as obrigações no **topo** do prompt (antes do header e dos arquivos) perturbavam a geração; no **fim** (após o contexto, posição de recência) o custo cai para ~1/6. CRR também melhora (0,81→0,92 e 0,69→0,80).

**Implicação de design:** `ConstraintSectionPlacement.Bottom` é o candidato a default do AECS (hoje o default é `Top`). Promoção exige o protocolo normal: reprovar em outro corpus/provider antes de virar default (ADR/experimento complementar).

**O que NÃO mudou:** o efeito graph-ranked vs naive permanece negativo neste corpus (−9/−8) em ambos os posicionamentos — fator separado, não confundido com o placement.

## Limitações (obrigatórias)

- Duas execuções sequenciais (não interleaved) — ordem/tempo podem confundir com drift do provider; replicar com interleaving antes da promoção.
- Mesmas limitações do corpus calibrado: 4 tarefas, provider único, temperatura default.
- CPVC bottom sem intervalo de confiança neste doc (agregador completo: $0,0139 n=80).

## Reprodução

```bash
# top (baseline)                          # bottom
dataset.cloud.json                        dataset.cloud-bottom.json
aecs experiment --dataset <tmp>/dataset.cloud-bottom.json --output <tmp>/out \
  --include-real-providers --allow-host-execution --resume \
  --evidence-root <tmp>/ev --key-directory <tmp>/keys
```

Parâmetro do dataset: `"constraintSectionPlacement": "bottom"` em **todas** as variantes (o protocolo exige parâmetros idênticos entre braços).

Docs anteriores: `2026-09-22-factorial-calibrated-n20.md` (achado top), `2026-09-21-factorial-corpus-cloud.md` (piso), `2026-09-21-factorial-cloud-provider.md` (teto).
