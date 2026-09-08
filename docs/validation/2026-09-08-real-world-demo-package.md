# Demonstração real reproduzível - 2026-09-08

## Resultado

A issue [#99](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/99) empacota a
demonstração AgronomoPlus em .NET 10 com comando versionado, artefatos fora do repositório e três
cenários determinísticos.

## Matriz demonstrada

| Cenário | Resultado |
| --- | --- |
| `agro-001-valid` | `Verified`, evidência recarregada e promoção autenticada verificada |
| `agro-002-failing-tests` | `Rejected` por gate `Tests` obrigatório |
| `agro-003-adversarial-scope` | `Rejected` por gate `Scope` |

O cenário válido altera handler e teste dentro do escopo. O cenário de teste falho altera somente o
teste esperado e deixa a implementação incorreta. O cenário adversarial tenta alterar Infrastructure,
fora do escopo autorizado.

## Evidência local

Ambiente: Windows `win-x64`, SDK .NET `10.0.400`, fixture AgronomoPlus `ecbbcac`.

| Verificação | Resultado |
| --- | --- |
| Categoria `RealWorldE2E` | passou; 1/1 teste xUnit |
| Script `demos/Run-RealWorldDemo.ps1` | passou |
| Relatório da execução direta | `C:\Users\ferna\AppData\Local\Temp\aecs-real-world-demo-validation-85b45879b9f24d3a8876a206d4d2057c\report.json` |
| Relatório do script | `C:\Users\ferna\AppData\Local\Temp\aecs-real-world-demo-script-939d89dbdbf6466687f390385fa1b940\report.json` |

Os artefatos foram gravados fora do checkout do AECS. O relatório da execução direta confirmou
`succeeded: true`, `originalRepositoryUnchanged: true` e `promotionVerified: true` para o cenário
válido.

## Limites

Os candidatos são determinísticos e demonstram o controle AECS. Medir capacidade de agente real
continua exigindo execução separada com provider real, custo e pré-registro próprios.
