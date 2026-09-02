# Gate causal offline do Adaptive Controller — 2026-09-02

## Resultado executivo

A issue #88 implementa o mecanismo de avaliação causal offline sem ativar roteamento adaptativo no
comando operacional `run`. O novo comando `adaptive-experiment` autentica uma recomendação shadow,
congela provider e protocolo e executa o plano fixo e o recomendado em braços staged isolados.

O mecanismo está pronto para receber um corpus real, mas ainda não demonstra benefício do Adaptive
Controller. A seleção, o pré-registro e a execução de pelo menos 50 tarefas reais pertencem à #89.
A #35 permanece bloqueada até essa evidência e ainda exigirá feature flag, canary e rollback.

## Roteamento e gates RSoul Factory

- discovery: `product-owner`, com análise especializada de `solution-architect`;
- solution: `solution-architect`, com revisão de segurança e provenance por `appsec-engineer`;
- build: `tech-lead`;
- verification/handoff: `qa-strategist`;
- `discovery_ready`: aprovado com valor, dependências e fora de escopo explícitos;
- `solution_ready`: aprovado com decisão arquitetural, interfaces e riscos registrados na ADR-023.

O escopo foi separado em duas issues para não confundir infraestrutura experimental com evidência:

- [#88](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/88): contrato, preflight,
  executor, checkpoints e análise;
- [#89](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/89): corpus e execução real.

## Evidência de implementação

- schema estrito `aecs.adaptive-offline-dataset/v1`, incluindo baseline, licença, cutoff, provider,
  hipótese, thresholds e política para referência zero;
- preflight fail-closed de recomendação e fontes autenticadas, com prevenção de leakage temporal;
- equivalência de capabilities/verificações e proibição de expansão de budget;
- braços com mesmo commit, tarefa, seed e provider, incluindo validação do adapter observado;
- checkpoints com fingerprint e retomada que não repete inferências persistidas;
- pares ausentes preservados no denominador e conclusões `Maintain`, `Adjust` ou `Abandon`;
- ADR-023 e documentação operacional do comando.

## Validação local

Ambiente de validação: imagem oficial `mcr.microsoft.com/dotnet/sdk:9.0`, com roll-forward para os
testes `net8.0`.

| Verificação | Resultado |
| --- | --- |
| build Release da solução | aprovado, 0 erros e 8 warnings preexistentes |
| testes específicos do gate | 11/11 aprovados em 5 s |
| suíte unitária | 363/363 aprovados em 6 s |
| integração curta, sem E2E/Docker/PostgreSQL | 106 aprovados; 1 falha ambiental |
| repetição do teste afetado após limpeza | 1/1 aprovado em 3 s |
| `git diff --check` | aprovado |
| formatter restrito aos arquivos da issue | aprovado |

A falha de integração não veio do código da #88. O fixture H1 copiou `bin/obj` ignorados, gerados
no Windows, e o MSBuild no container tentou resolver um fallback path do Visual Studio. Somente os
dois diretórios gerados e ignorados foram removidos; o teste afetado passou em seguida. Nenhum fonte
ou arquivo versionado foi removido.

O `dotnet format --verify-no-changes` global continua acusando dívida de whitespace em arquivos
preexistentes fora da #88. A formatação desta mudança foi aplicada com `--include` para não misturar
uma reescrita transversal neste PR.

## Riscos residuais e próximo handoff

- não há ainda resultado causal com provider real;
- a recomendação shadow ainda não carrega identidade própria de provider; o dataset congela um
  provider único para os dois braços;
- execução local e cloud devem usar manifests e relatórios separados;
- licença e autorização de redistribuição precisam ser verificadas durante a curadoria da #89;
- nenhum resultado deste gate pode ativar automaticamente o controller.

Handoff: `qa-strategist` valida a estratégia de coleta da #89 e seus critérios de aceite antes da
primeira inferência. Depois, a decisão arquitetural volta à #35 com o relatório autenticado, sem
reclassificar os resultados H1 como evidência adaptativa.
