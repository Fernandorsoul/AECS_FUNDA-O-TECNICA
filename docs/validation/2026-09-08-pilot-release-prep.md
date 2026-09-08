# Preparação do release piloto - 2026-09-08

## Resultado

A issue [#102](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/102) recebeu pacote de
preparação para release versionado: script de publicação/checksum/inventário, notas template e
checklist go/no-go.

## Evidência local

Ambiente: Windows `win-x64`, SDK .NET `10.0.400`.

| Grupo | Resultado |
| --- | --- |
| Ensaio de pacote com `-AllowDirty` em `%TEMP%` | Passou; gerou ZIP, manifest e `SHA256SUMS.txt` |
| Build Release | Passou durante `dotnet publish` do ensaio |
| `git diff --check HEAD` | Passou |

## Limite operacional

Esta branch não publica release, não escolhe o SHA final e não registra aceite independente. O
release final depende dos PRs da entrega piloto mesclados em `dev`, CI completo verde, pacote gerado
do mesmo SHA e aprovação do responsável.
