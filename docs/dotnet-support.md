# Matriz de suporte .NET

Esta página é a fonte operacional para versões .NET do AECS. A matriz abaixo descreve o que o
repositório implementa e valida; menções diferentes em documentos fundacionais ou relatórios de
validação antigos representam histórico, não a capacidade vigente.

## Matriz vigente

| Camada | Versão | Autoridade no repositório |
| --- | --- | --- |
| Projetos AECS | `net10.0` | `TargetFramework` dos quatro projetos em `src/` e das duas suites em `tests/` |
| Execução framework-dependent | runtime .NET 10 LTS estável | TFM `net10.0`; preview não é aceito pela política do repositório |
| Build, testes e ferramentas | SDK .NET 10 LTS estável | `global.json` usa `10.0.100`, `latestFeature` e recusa previews |
| Sandbox staged padrão | SDK .NET 10 | imagem OCI imutável definida em `SandboxExecutionProfile.DefaultImage` e fixada por digest |
| Fixture Docker E2E | `net10.0` | projeto temporário criado por `DockerSandboxE2ETests` |
| Próxima revisão | patches .NET 10 LTS | atualizar dentro do mesmo major LTS, mantendo `allowPrerelease: false` |

Uma reprodução suportada do estado atual deve registrar um SDK estável `10.0.x`, runtime 10
disponível para os assemblies `net10.0` e exatamente a imagem Docker fixada pelo contrato. Build
feito com SDK preview é diagnóstico de compatibilidade, não evidência de que a matriz
vigente foi reproduzida.

## Suporte oficial confirmado

Matriz verificada em 2026-10-01 contra fontes oficiais da Microsoft:

- .NET 10 é LTS ativo; a página oficial de downloads lista o SDK recomendado `10.0.x`, com suporte
  até novembro de 2028.
- .NET 8 LTS e .NET 9 STS estão em manutenção apenas até novembro de 2026.

Fontes:
[downloads .NET](https://dotnet.microsoft.com/en-us/download),
[downloads .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) e
[política de suporte .NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Dependências auditadas na migração

| Dependência | Decisão |
| --- | --- |
| Roslyn/MSBuild | Manter `Microsoft.CodeAnalysis.*` em `5.0.0`, já compatível com a análise C# atual, e validar por testes de grafo Roslyn/MSBuild. |
| Microsoft.Build.Locator | Manter `1.7.8`; o pacote localiza a instância MSBuild resolvida pelo SDK instalado e não força TFM antigo. |
| EF Core | Usar `Microsoft.EntityFrameworkCore.Design` versão `10.0.4`, alinhado ao provider PostgreSQL e ao major do runtime. |
| Npgsql EF Core provider | Usar `Npgsql.EntityFrameworkCore.PostgreSQL` versão `10.0.3`, compatível com EF Core 10. |
| YamlDotNet | Manter `18.1.0`; não há acoplamento ao TFM antigo. |

Schemas de evidência, hashes, manifestos e relatórios históricos não são reescritos. Replays de
evidências antigas podem apontar divergência de ambiente quando o runtime/toolchain observado não
coincidir com o original; isso é esperado e deve aparecer como evidência, não como reclassificação
do resultado antigo.

## Evidência mínima de reprodução

O passo de reprodução operacional deve publicar, junto dos resultados:

- saída de `dotnet --info` e resolução efetiva do `global.json`;
- restore, build e suites com SDK 10 estável, sem fallback para outro major;
- runtime 10 disponível para os assemblies `net10.0`;
- identidade e digest observado da imagem Docker staged;
- resultado das categorias Docker e PostgreSQL, com versões do daemon e do servidor;
- distinção entre falha de código, dependência indisponível e divergência de ambiente.