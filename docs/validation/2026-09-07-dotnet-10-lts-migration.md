# Reversão para .NET 8 LTS - 2025-07-13

## Resultado esperado

> Registro historico substituido em 2026-10-01. A reversao para .NET 8 descrita abaixo foi abandonada; consulte [validacao atual](2026-10-01-orbis-preparation.md) e [matriz atual](../dotnet-support.md).

Esta validação acompanha a preparação para integração com o Orbis. O objetivo é estabilizar o
projeto usando .NET 8 LTS (versão estável suportada) em vez de .NET 10 preview, garantindo
reproduibilidade e compatibilidade.

## Matriz antes/depois

| Camada | Antes | Depois |
| --- | --- | --- |
| Projetos AECS | `net10.0` (preview) | `net8.0` (LTS) |
| SDK do repositório | `10.0.400-preview.0.26322.102` | `8.0.410` |
| Preview | aceito (`allowPrerelease: true`) | recusado (`allowPrerelease: false`) |
| Runtime requerido | .NET 10 preview | .NET 8 LTS |
| RollForward | `latestMajor` | `latestPatch` |
| EF Core/Npgsql provider | `10.0.4` / `10.0.3` | `8.0.11` / `8.0.10` |

## Justificativa

.NET 10 está em preview (lançamento estável previsto para novembro 2025). Para garantir:
- Reproduibilidade em ambientes de CI/CD
- Compatibilidade com consumidores (Orbis)
- Estabilidade para produção

A versão .NET 8 LTS é a escolha mais segura com suporte até novembro 2026.

## Fontes oficiais consultadas

- Downloads oficiais .NET: https://dotnet.microsoft.com/en-us/download
- Downloads .NET 8: https://dotnet.microsoft.com/en-us/download/dotnet/8.0
- Política de suporte .NET: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core

## Arquivos alterados

- `global.json` - SDK version, rollForward, allowPrerelease
- Todos os `.csproj` em `src/` e `tests/` - TargetFramework
- `.github/workflows/ci.yml` - dotnet-version
- `.github/workflows/aecs-pipeline.yml` - DOTNET_VERSION
- `.github/workflows/context-compiler-h1.yml` - dotnet-version
- `src/AECS.Infrastructure/AECS.Infrastructure.csproj` - EF Core/Npgsql versions
- `tests/fixtures/**/*.csproj` - TargetFramework
- `experiments/context-compiler-h1/repository/Benchmark.csproj` - TargetFramework
- Documentação atualizada em `docs/`

## Evidência a publicar no PR

- `dotnet --info` com SDK/runtime 8 estáveis.
- `dotnet restore AECS.slnx`.
- `dotnet build AECS.slnx --configuration Release --no-restore --nologo`.
- `dotnet test AECS.slnx --configuration Release --no-build --nologo`.
- Docker, PostgreSQL e RealWorldE2E quando o ambiente disponibilizar daemon/serviço.

## Limites

Este relatório não reexecuta nem reclassifica experimentos H1 históricos. Evidências antigas devem
continuar legíveis pelos schemas existentes; divergência de runtime/toolchain em replay deve ser
relatada como divergência ambiental.
