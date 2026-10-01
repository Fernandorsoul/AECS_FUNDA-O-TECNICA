# Preparacao AECS para Orbis - 2026-10-01

Responsavel pelas correcoes: backend-engineer. Proximo responsavel: qa-strategist.

## Alteracoes

- Mantido o SDK .NET 10 estavel do global.json e os projetos net10.0 existentes.
- Dockerfile alinhado ao SDK 10, com imagem imutavel por digest e comandos AECS.slnx.
- Contexto Docker inclui os fixtures, tarefas, scripts e documentos exigidos pelos testes; fontes de credenciais e artefatos locais permanecem excluidas.
- OpenTelemetry e exportador OTLP atualizados para 1.15.3, corrigindo os avisos GHSA-g94r-2vxg-569j e GHSA-4625-4j76-fww9 encontrados no restore.
- Exemplo de contrato ligado ao verificador Tests e protegido por teste com o parser real.
- Relatorios anteriores marcados como historicos, sem apagar seu conteudo.

## Verificacao

- Dockerfile compilado com SDK .NET 10: zero erros e quatro avisos xUnit1031 preexistentes em RejectionFlowTests.
- 459 testes unitarios aprovados, zero falhas e zero skips.
- Execucao conjunta final no container corrigido: 459 testes unitarios e 130 testes de integracao aprovados, zero falhas e zero skips. Integracao concluida em 2 minutos e 12 segundos. As categorias RealWorldE2E, PostgreSql e DockerSandbox foram excluidas explicitamente.
- Auditoria dotnet list AECS.slnx package --vulnerable --include-transitive: nenhum pacote vulneravel reportado nas fontes consultadas para os seis projetos.
- git diff --check sem erros de whitespace.

O estagio Docker de testes cria um commit sintetico para os testes que abrem worktrees; nao copia o Git do host nem altera o estagio CLI. A primeira execucao conjunta ficou sem progresso no teste AcceptanceCriteriaExecutionTests. O build da worktree desse teste passou a usar --disable-build-servers; o teste isolado passou em 31 segundos. Esta alteracao e de isolamento do teste, nao uma correcao geral do runner de processos.

## Limites

Esta preparacao nao demonstra prontidao de producao. Fluxo com provedor real, PostgreSQL, sandbox Docker staged e promocao autorizada precisam de verificacao separada. O Angular precisa de verificadores proprios; a analise semantica atual permanece centrada em C#.
