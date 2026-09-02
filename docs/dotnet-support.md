# Matriz de suporte .NET

Esta página é a fonte operacional para versões .NET do AECS. A matriz abaixo descreve o que o
repositório implementa e valida; menções diferentes em documentos fundacionais ou prompts antigos
representam intenção histórica, não uma capacidade presente.

## Matriz vigente

| Camada | Versão | Autoridade no repositório |
| --- | --- | --- |
| Projetos AECS | `net8.0` | `TargetFramework` dos quatro projetos em `src/` e das duas suites em `tests/` |
| Execução framework-dependent | runtime .NET 8 estável e atualizado | TFM `net8.0`; o SDK 9 isolado não substitui automaticamente o runtime 8 do host |
| Build, testes e ferramentas | SDK .NET 9 estável | `global.json` parte de `9.0.100`, usa `latestFeature` e recusa previews; o CI instala `8.0.x` e `9.0.x` |
| Sandbox staged padrão | SDK .NET 9 | imagem OCI imutável definida em `SandboxExecutionProfile.DefaultImage` e fixada por digest |
| Fixture Docker E2E | `net9.0` | projeto temporário criado por `DockerSandboxE2ETests`; não é o TFM do produto |
| Próxima migração | .NET 10 LTS | trabalho futuro obrigatório; ainda não é a configuração implementada |

Uma reprodução suportada do estado atual deve registrar um SDK estável `9.0.x`, disponibilizar o
runtime 8 para os binários `net8.0` e usar exatamente a imagem Docker fixada pelo contrato. Build
feito apenas com SDK 10 — especialmente preview — é diagnóstico de compatibilidade, não evidência
de que a matriz vigente foi reproduzida.

## Janela e obrigação de migração

Matriz verificada em 2026-09-02 contra a
[política oficial de suporte do .NET](https://dotnet.microsoft.com/en-us/platform/support/policy):

- .NET 8 LTS está em manutenção e termina o suporte em 2026-11-10;
- .NET 9 STS está em manutenção e termina o suporte em 2026-11-10;
- .NET 10 LTS está ativo e tem suporte até novembro de 2028.

Esta decisão é, portanto, transitória. O AECS deve migrar toolchain, runtime do produto, imagem do
sandbox, CI e fixtures para .NET 10 LTS antes de 2026-11-10. Depois dessa data, a matriz acima não
pode ser apresentada como configuração suportada para produção, mesmo que ainda compile.

O [ADR-020](adr/ADR-020-dotnet-support-matrix.md) registra a decisão e separa a correção documental
da futura migração técnica.

## Evidência mínima de reprodução

O passo de reprodução operacional deve publicar, junto dos resultados:

- saída de `dotnet --info` e resolução efetiva do `global.json`;
- restore, build e suites com SDK 9, sem fallback para outro major;
- runtime 8 disponível para os assemblies `net8.0`;
- identidade e digest observado da imagem Docker staged;
- resultado das categorias Docker e PostgreSQL, com versões do daemon e do servidor;
- distinção entre falha de código, dependência indisponível e divergência de ambiente.
