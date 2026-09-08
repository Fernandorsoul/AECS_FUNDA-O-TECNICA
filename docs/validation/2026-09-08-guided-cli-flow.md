# Fluxo guiado CLI/Jarvis - 2026-09-08

## Resultado

A issue [#98](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/98) adiciona um guia
operacional embutido no Jarvis e melhora a saída de execução para conectar contrato, fases, budget,
decisão, evidência, revisão, exportação e promoção controlada.

## Evidência local

Ambiente: Windows `win-x64`, SDK .NET `10.0.400`.

| Grupo | Resultado |
| --- | --- |
| Build Release | Passou; 4 warnings legados `xUnit1031` em `RejectionFlowTests` |
| Testes unitários focados em Jarvis, TaskContract e VS Code | 86/86 passaram |
| Testes unitários de orçamento e retries | 17/17 passaram |
| Smoke manual `jarvis guide` | Passou |
| Integração de promoção, replay, evidência autenticada e falhas staged | 56/56 passaram |
| `git diff --check HEAD` | Passou |

## Cenários cobertos pelo desenho

- contrato inválido falha antes da execução e orienta correção do campo;
- `guide` documenta preflight, contrato, execução, retomada, revisão, exportação e promoção;
- `run` mostra fases e budget consumido sem classificar parcial/interrupção como sucesso final;
- revisão preserva aprovação/rejeição/abandono explícitos e exige `PROMOTE <diff-hash>` para aplicar;
- VS Code permanece compatível e continua sem endpoint de promoção.
