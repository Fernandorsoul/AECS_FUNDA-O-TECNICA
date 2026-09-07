# Migração operacional para .NET 10 LTS - 2026-09-07

## Resultado esperado

Esta validação acompanha a issue
[#96](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/96). O objetivo é migrar produto,
testes, CI, sandbox staged e fixtures mantidas pelo AECS para .NET 10 LTS sem aceitar SDK preview ou
fallback silencioso para outro major.

## Matriz antes/depois

| Camada | Antes | Depois |
| --- | --- | --- |
| Projetos AECS | `net8.0` | `net10.0` |
| SDK do repositório | `9.0.100` com `latestFeature` | `10.0.100` com `latestFeature` |
| Preview | recusado | recusado |
| Runtime requerido | .NET 8 | .NET 10 |
| Imagem staged | `mcr.microsoft.com/dotnet/sdk:9.0@sha256:f190d2dd9eef2899c91ac323caa0bd2b39334a5400ba93013e5199da39dad940` | `mcr.microsoft.com/dotnet/sdk:10.0@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510` |
| Fixture Docker E2E | `net9.0` | `net10.0` |
| EF Core/Npgsql provider | major 8 | EF Core Design `10.0.4` e Npgsql EF provider `10.0.3` |

## Fontes oficiais consultadas

- Downloads oficiais .NET: https://dotnet.microsoft.com/en-us/download
- Downloads .NET 10: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- Política de suporte .NET: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core

Em 2026-09-07, .NET 10 aparece como LTS ativo, com SDK recomendado `10.0.400` lançado em
2026-08-11, e suporte até novembro de 2028. .NET 8 e .NET 9 aparecem em manutenção até novembro de
2026.

## Evidência a publicar no PR

- `dotnet --info` com SDK/runtime 10 estáveis.
- `dotnet restore AECS.slnx`.
- `dotnet build AECS.slnx --configuration Release --no-restore --nologo`.
- `dotnet test AECS.slnx --configuration Release --no-build --nologo --filter "Category!=RealWorldE2E&Category!=PostgreSql&Category!=DockerSandbox"`.
- `pwsh ./tests/scripts/Test-ExperimentToolchain.ps1`.
- Pull da imagem staged fixada por digest.
- Docker, PostgreSQL e RealWorldE2E quando o ambiente disponibilizar daemon/serviço.

## Execução local observada

Ambiente local de validação: Windows `win-x64`, SDK .NET `10.0.400`, host runtime
`Microsoft.NETCore.App 10.0.11`, instalação temporária em `%TEMP%/aecs-dotnet-10-stable`.

| Verificação | Resultado |
| --- | --- |
| `dotnet restore AECS.slnx` | passou |
| `dotnet build AECS.slnx --configuration Release --no-restore --nologo` | passou; 4 avisos xUnit1031 preexistentes |
| Unitários `Category!=RealWorldE2E&Category!=PostgreSql&Category!=DockerSandbox` | 369/369 passaram |
| Integrações de aceite, snapshot e Roslyn | 12/12 passaram |
| Integrações de evidência, graph, histórico e Jarvis | 33/33 passaram |
| Integrações de promoção e replay | 29/29 passaram |
| Integrações de pipeline, runtime e contexto real | 33/33 passaram |
| Integrações adaptativas multi-baseline | 3/3 passaram |
| `pwsh ./tests/scripts/Test-ExperimentToolchain.ps1` | passou |
| VS Code client `npm ci && npm test` | passou; 2/2 testes e 0 vulnerabilidades |
| `git diff --check HEAD` | passou |
| `docker pull` da imagem staged | não executado localmente: daemon Docker Desktop indisponível |

O pull e os testes que dependem de Docker/PostgreSQL/RealWorldE2E ficam para CI ou ambiente local com
daemon e serviços disponíveis.

## Limites

Este relatório não reexecuta nem reclassifica experimentos H1 históricos. Evidências antigas devem
continuar legíveis pelos schemas existentes; divergência de runtime/toolchain em replay deve ser
relatada como divergência ambiental.
