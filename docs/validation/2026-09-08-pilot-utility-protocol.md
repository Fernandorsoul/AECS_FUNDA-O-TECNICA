# Protocolo do piloto de utilidade - 2026-09-08

## Resultado

A issue [#100](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/100) recebeu um pacote
de execução auditável para validar utilidade com 10 tarefas reais e revisão independente sem
simular evidência humana ou uso de provider real.

## Evidência local

Ambiente: Windows `win-x64`, SDK .NET `10.0.400`.

| Grupo | Resultado |
| --- | --- |
| Validação do manifesto-template com `-AllowTemplatePlaceholders` | Passou |
| Validação negativa com `-RequireFrozen -RequireCompleted` | Falhou fechado como esperado, apontando hash de registro, 10 tarefas/categorias, decisão final e relatório ausentes |
| Build Release | Passou; 4 warnings legados `xUnit1031` em `RejectionFlowTests` |
| `git diff --check HEAD` | Passou |

## Limite operacional

Esta branch não executa provider real, não gasta credenciais e não registra revisão independente no
lugar de uma pessoa. O pacote falha fechado quando esses campos estiverem ausentes na coleta final.
