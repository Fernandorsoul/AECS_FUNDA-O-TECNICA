# Baseline da entrega piloto AECS

Documento de controle da issue
[#95](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/95), parte da entrega
[#94](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/94).

## Baseline congelada

| Campo | Valor |
| --- | --- |
| Data de congelamento | 2026-09-07 |
| Branch base | `dev` |
| SHA base | `eafb736f5554d1fc4e9b1b94b77a8ff81ac347d5` |
| Origem do SHA | Merge do PR [#93](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/pull/93) |
| CI confirmado | `build-test-smoke` e `real-world-e2e` concluídos com sucesso no run `34151065711` |

Essa baseline define o ponto inicial da entrega piloto. PRs posteriores devem apontar para
`dev`, declarar a issue que atendem e manter rastreabilidade entre código, documentação, testes e
evidência de validação.

## Escopo do piloto

O piloto entrega o AECS como plano de controle experimental para tarefas de engenharia C#/.NET. O
fluxo principal é CLI/Jarvis, com contrato explícito de tarefa, execução em worktree Git isolada,
sandbox Docker por padrão, verificação determinística, evidência assinada e promoção/exportação por
ação posterior explícita.

A plataforma inicial de uso é Windows x64. Linux permanece suportado como ambiente de CI e de
execução de containers, mas não é declarado como plataforma de usuário final até receber validação
própria. O backend padrão de evidência local é JSON; PostgreSQL é opção documentada e deve falhar
fechado quando selecionado sem configuração válida.

O Adaptive Controller permanece em shadow mode. Ele pode calcular recomendações e gerar evidências
offline, mas a configuração padrão não permite que ele altere modelo, contexto, orçamento,
capabilities, gates ou promoção da execução operacional fixa.

## Funcionalidades disponíveis na baseline

- Contratos `aecs.task-contract/v1` com schema estrito, risco efetivo e fingerprint canônico.
- Preflight de baseline em worktree descartável, snapshot determinístico e grafo semântico C# por
  Roslyn/MSBuild.
- Compilação de contexto limitada por escopo, orçamento e manifesto versionado.
- Execução com Ollama local, fallback compatível com OpenAI somente por opt-in explícito e provider
  mock para demonstração do fluxo de controle.
- Gates obrigatórios de agente, aplicação, diff não vazio, escopo, orçamento, build/testes quando
  exigidos e critérios de aceite verificáveis.
- Verificadores semânticos EB001-EB005, incluindo histórico revisado e persistido.
- Evidência autenticada em JSON ou PostgreSQL, replay, graph queries, exportação de patch e promoção
  controlada.
- Jarvis com histórico durável, revisão humana, exportação e confirmação literal antes de promover.
- Cliente VS Code mínimo para iniciar contratos e revisar resultados sem aplicar patches.
- Harness experimental com datasets versionados, análise pareada, política de baseline zero e
  suporte a corpus multi-baseline externo.

## Limitações declaradas

- A matriz vigente ainda usa projetos `net8.0`, SDK .NET 9 estável e runtime .NET 8; a migração para
  .NET 10 LTS é obrigatória antes do release piloto final.
- A baseline é de protótipo experimental, não de produto pronto para produção.
- `Verified` comprova apenas o contrato e os gates executados; não prova requisitos que não foram
  declarados no contrato.
- Provider mock valida o fluxo de controle, mas não mede qualidade de alteração real.
- Estudos H1 locais e cloud não habilitam automaticamente roteamento adaptativo.
- A execução real com cloud depende de credencial, custo e pré-registro próprios.

## Reconciliação das issues abertas históricas

| Issue | Estado observado em `dev` | Evidência |
| --- | --- | --- |
| [#80](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/80) | Implementada em `dev`; falta apenas reconciliar/fechar a issue com evidência. | PR #81, commit `8ba89fb`, docs de verificação semântica e regressões do Context Compiler. |
| [#82](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/82) | Implementada em `dev`; falta apenas reconciliar/fechar a issue com evidência. | PR #83, commit `e774ecb`, workflow `real-world-e2e` separado e relatório de reprodução da stack. |
| [#84](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/84) | Evidência local publicada; falta reconciliar se o aceite exige expansão além do piloto. | PR #85, commits `0ec45c9` e `1d82973`, relatório `docs/validation/2026-09-02-local-h1-context-v2.md`. |
| [#86](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/86) | Implementada em `dev`; falta apenas reconciliar/fechar a issue com evidência. | PR #87, commit `f529384`, ADR-022 e documentação do harness experimental. |
| [#88](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/88) | Implementada em `dev`; falta apenas reconciliar/fechar a issue com evidência. | PR #90, commit `514f7ef`, ADR-023 e `adaptive-experiment`. |
| [#91](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/91) | Implementada em `dev`; falta apenas reconciliar/fechar a issue com evidência. | PR #92, commit `c8bfe7d`, ADR-024 e relatório de prontidão do corpus real. |

Essas issues não bloqueiam o produto por falta de código conhecido na baseline, mas bloqueiam a
higiene do release enquanto permanecerem abertas sem comentário de fechamento ou evidência revisada.

## Plano de issues da entrega

| Issue | Bloqueia release? | Dependência principal | Resultado esperado |
| --- | --- | --- | --- |
| [#95](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/95) | Sim | PR #93 integrado | Baseline e escopo congelados. |
| [#96](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/96) | Sim | #95 | Stack operacional migrada para .NET 10 LTS. |
| [#97](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/97) | Sim | #95, #96 | Instalação e diagnóstico reproduzíveis. |
| [#98](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/98) | Não, mas recomendado | #97 | Fluxo guiado de tarefa, revisão e promoção no Jarvis/CLI. |
| [#99](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/99) | Sim | #96, #97 | Demonstração real reproduzível com aceite, rejeição e escopo. |
| [#101](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/101) | Sim | #95, #96 | Recuperação de falhas e integridade validadas. |
| [#100](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/100) | Sim para aceite de utilidade | #98, #99, #101 | Piloto com 10 tarefas reais e revisão independente. |
| [#102](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/102) | Sim | #95-#101 | Release piloto versionado com pacote, docs e aceite. |

## Responsáveis

| Papel | Responsável |
| --- | --- |
| Implementação das issues de entrega | Codex, em branches `codex/issue-<numero>-<slug>` |
| Revisão técnica dos PRs | Mantenedor do repositório `Fernandorsoul/AECS_FUNDA-O-TECNICA` |
| Aceite funcional do piloto | Fernando / owner do produto |
| Aceite de release | Fernando / owner do produto, após evidências das issues #95-#102 |

## Critério de prontidão para release piloto

O release piloto só deve ser versionado quando:

1. as issues #95, #96, #97, #99, #100, #101 e #102 estiverem concluídas com PRs aceitos em `dev`;
2. a documentação apontar para o mesmo SHA de baseline/release usado nas validações;
3. existir demonstração reproduzível de execução `run`, evidência, revisão e promoção/exportação;
4. falhas críticas, corrupção de evidência, timeout, orçamento e divergência de baseline tiverem
   testes ou relatórios publicados;
5. limitações residuais estiverem explícitas no release notes.

