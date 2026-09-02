# Otimização do E2E no fluxo de desenvolvimento — 2026-09-02

## Problema observado

O workflow `CI` executava o E2E reproduzível depois de build, testes comuns, sandbox Docker e
PostgreSQL, no mesmo job com timeout global de 20 minutos. Se o E2E atrasasse, os guards e smokes
seguintes não eram executados. No PR #73, um hang transitório consumiu os minutos restantes do job
e cancelou toda a cauda do workflow.

A suíte também executava o candidato válido AgronomoPlus duas vezes: uma no cenário versionado e
outra em um teste separado de promoção. A medição local, excluindo build, foi:

| Execução anterior | Tempo de processo |
| --- | ---: |
| Cenários versionados | 42,677 s |
| Promoção duplicada | 28,675 s |
| Total | 71,352 s |

## Alteração

- a promoção pelo diff autenticado ocorre na mesma execução válida já produzida pelo cenário;
- a fixture v1.1 declara explicitamente qual cenário exige promoção;
- o relatório registra `promotionRequired` e `promotionVerified`;
- o E2E roda em job paralelo, com restore/build mínimos e timeout próprio de oito minutos;
- `dotnet test --blame-hang` interrompe um hang após cinco minutos e publica o dump junto do
  relatório e das evidências;
- o job principal continua executando guards e smokes independentemente do E2E.

## Resultado local

A categoria otimizada executou um único teste, dois cenários, promoção autenticada e persistida,
relatório v1.1 e todas as verificações em **41,574 s**. O comando final do CI, já com o coletor
`--blame-hang`, levou **41,916 s**. A redução observada com esse comando foi de **29,436 s
(41,3%)** sobre a linha de base, sem retirar cobertura funcional.

O ganho de fluxo no GitHub Actions é adicional: setup e execução E2E passam a ocorrer em paralelo
ao job principal, e um travamento não cancela os smokes não relacionados. Rastreabilidade:
[issue #82](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/82).
