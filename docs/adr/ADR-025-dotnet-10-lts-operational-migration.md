# ADR-025: Migração operacional para .NET 10 LTS

Status: Accepted

Date: 2026-09-07

## Context

O ADR-020 congelou uma matriz transitória com projetos `net8.0`, SDK .NET 9 e sandbox staged
.NET 9. Essa matriz era suficiente para estabilizar a reprodução, mas .NET 8 e .NET 9 entram no fim
de suporte em novembro de 2026. A entrega piloto precisa estar em uma linha LTS antes do release.

As fontes oficiais consultadas em 2026-09-07 listam .NET 10 como LTS ativo, SDK recomendado
`10.0.400` lançado em 2026-08-11 e suporte até novembro de 2028.

## Decision

Migrar a matriz operacional do AECS para .NET 10 LTS:

- projetos de produção, CLI e testes passam a compilar para `net10.0`;
- `global.json` passa a exigir SDK `10.0.100`, com `rollForward: latestFeature` e
  `allowPrerelease: false`;
- CI e workflow H1 instalam apenas `10.0.x`;
- a imagem staged padrão passa a ser
  `mcr.microsoft.com/dotnet/sdk:10.0@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510`;
- fixtures Docker mantidas pelo AECS passam a usar `net10.0`;
- EF Core e Npgsql EF provider são atualizados para versões major 10;
- Roslyn/MSBuild e demais bibliotecas sem acoplamento ao TFM antigo são preservadas e validadas por
  testes.

Schemas e evidências históricas permanecem imutáveis. Resultados produzidos sob a matriz 8/9 não são
reinterpretados depois da migração.

## Consequences

A reprodução suportada agora exige SDK e runtime .NET 10 estáveis. Hosts com SDK 10 preview, SDK 11
ou apenas SDK 9 devem falhar antes da validação, em vez de produzir uma evidência ambígua.

Replays de evidências antigas podem reportar divergência de ambiente quando ferramenta, runtime ou
imagem staged não coincidirem com a execução original. Esse comportamento preserva rastreabilidade e
não altera a decisão histórica.
