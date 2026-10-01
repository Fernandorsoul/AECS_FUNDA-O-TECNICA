# ADR-026: Reversão para .NET 8 LTS para estabilização Orbis

Status: Superseded

Superseded on 2026-10-01: the preview SDK was the problem, not net10.0. The current matrix uses stable .NET 10 (see global.json and docs/dotnet-support.md). The original rationale below is retained as historical context and must not guide installation.

Date: 2025-07-13

## Context

O ADR-025 migrou a matriz operacional do AECS para .NET 10 LTS, mas .NET 10 ainda está em preview
(lançamento estável previsto para novembro 2025). Para a preparação da integração com o Orbis, é
necessário usar uma versão estável e suportada.

O Orbis terá backend C#/.NET e interface Angular 17. A integração requer que o AECS seja
executável em ambientes de produção com SDK estável.

## Decision

Reverter a matriz operacional do AECS para .NET 8 LTS:

- projetos de produção, CLI e testes passam a compilar para `net8.0`;
- `global.json` passa a exigir SDK `8.0.410`, com `rollForward: latestPatch` e
  `allowPrerelease: false`;
- CI e workflow H1 instalam apenas `8.0.x`;
- fixtures Docker mantidas pelo AECS passam a usar `net8.0`;
- EF Core e Npgsql EF provider são atualizados para versões major 8 (`8.0.11` / `8.0.10`);
- Roslyn/MSBuild e demais bibliotecas sem acoplamento ao TFM antigo são preservadas e validadas por
  testes.

Schemas e evidências históricas permanecem imutáveis. Resultados produzidos sob a matriz 10 não são
reinterpretados depois da reversão.

## Consequences

A reprodução suportada agora exige SDK e runtime .NET 8 estáveis. Hosts com SDK preview ou outro
major devem falhar antes da validação, em vez de produzir uma evidência ambígua.

Replays de evidências antigas podem reportar divergência de ambiente quando ferramenta, runtime ou
imagem staged não coincidirem com a execução original. Esse comportamento preserva rastreabilidade e
não altera a decisão histórica.

A migração para .NET 10 LTS será reavaliada quando o SDK estável for lançado (previsto para novembro
2025).
