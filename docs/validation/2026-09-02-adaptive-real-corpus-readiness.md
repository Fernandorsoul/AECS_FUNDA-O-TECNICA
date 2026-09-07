# Prontidão para corpus adaptativo real — 2026-09-02

## Resultado executivo

A discovery da #89 concluiu que o corpus atual não suporta uma execução honesta de 50 tarefas
reais. O AECS possui 15 TaskContracts versionados, quatro sob `tasks/real-project`, 43 issues no
GitHub e nenhuma coleção local de 50 recomendações shadow autenticadas.

O protocolo v1 também congelava uma única baseline. Isso impediria usar issues históricas, que
possuem commit-base e oráculo próprios. A issue #91 e o protocolo
`aecs.adaptive-offline-dataset/v2` removem essa limitação sem baixar nem versionar corpus externo.

## Opções avaliadas

| Fonte | Vantagem | Limitação para a #89 |
| --- | --- | --- |
| AECS atual | domínio diretamente relevante | amostra menor que 50 e sem recomendações suficientes |
| SWE-bench | issues reais, commits e oráculos executáveis | multi-repositório; conjuntos públicos avaliados não cobrem C# |
| InferredBugs | bugs e fixes C# com licença de dataset MIT | snapshots before/after sem harness executável completo |
| RunBugRun | mais de 700 mil pares executáveis, incluindo C# | programas curtos e licenças delegadas às fontes |

Fontes primárias:
[SWE-bench](https://github.com/SWE-bench/SWE-bench/blob/main/docs/guides/datasets.md),
[SWE-bench Multilingual](https://www.swebench.com/multilingual.html),
[InferredBugs](https://github.com/microsoft/InferredBugs) e
[RunBugRun](https://github.com/giganticode/run_bug_run).

Nenhum dataset, patch ou repositório de terceiros foi copiado para o AECS durante esta análise.

## Implementação da #91

- schema v2 com catálogo de checkouts externos e provenance/licença explícitas;
- `repositoryId`, hash do TaskContract, upstream, baseline derivada e oráculo por tarefa;
- oráculo limitado a um commit filho direto, hash de diff binário e paths test-only;
- imagem Docker por digest igual no manifesto e no contrato;
- paths do oráculo obrigatoriamente proibidos ao agente;
- remote `origin`, root do checkout, status limpo, `HEAD`, parent, diff e paths revalidados antes do
  provider;
- revalidação do source e do contrato antes de aceitar checkpoints no resume;
- relatório v2 com origem, licença, upstream, baseline, hash do oráculo e imagem por par;
- `goldPatch` ausente do schema e rejeitado como campo desconhecido;
- compatibilidade mantida para datasets v1 single-baseline.

## Roteamento e gates RSoul Factory

- discovery: `product-owner`;
- solution: `solution-architect`;
- build: `tech-lead`;
- verification: `qa-strategist`;
- `discovery_ready`, `solution_ready`, `build_ready` e `verification_ready`: aprovados.

## Validação

Ambiente: imagem oficial `mcr.microsoft.com/dotnet/sdk:9.0`.

| Verificação | Resultado |
| --- | --- |
| build Release da solução | aprovado, 0 erros e 8 warnings preexistentes |
| testes unitários adaptativos | 17/17 aprovados em 5 s |
| suíte unitária completa | 369/369 aprovados em 6 s |
| integração Git multi-repositório | 3/3 aprovados em 3 s |
| formatter restrito aos arquivos da mudança | aprovado |
| `git diff --check` | aprovado |

As integrações criam dois repositórios reais temporários, com origins, upstreams, baselines e
oráculos distintos. Elas verificam aceitação válida, rejeição de hash adulterado, preservação dos
checkouts, provenance no relatório, falha antes do provider e recusa de resume após tamper.

## Estado da #89

A #91 torna tecnicamente possível representar as 50 tarefas, mas a #89 ainda não está pronta para
inferência. Faltam:

1. escolher um corpus compatível com o objetivo do AECS;
2. auditar licença e custo de execução por instância;
3. preparar 50 checkouts e oráculos externos estáveis;
4. produzir histórico suficiente e 50 recomendações shadow autenticadas;
5. pré-registrar o estudo local respeitando o gate de memória do hardware alvo;
6. executar local e cloud em estudos separados.

Os modelos `qwen2.5-coder:1.5b` e `qwen2.5-coder:3b` continuam instalados, mas a avaliação anterior
reprovou o 3B em qualidade e margem de RAM. Portanto, as 100 inferências pareadas não devem começar
sem novo pré-registro de recurso. Não há credencial cloud disponível no fluxo atual.

A #35 permanece bloqueada. A conclusão desta etapa é prontidão de infraestrutura, não evidência de
benefício adaptativo.
