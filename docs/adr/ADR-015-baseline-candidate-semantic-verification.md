# ADR-015: Comparar baseline e candidato com relações Roslyn

- Status: Accepted
- Date: 2026-08-31

## Context

EB001–EB004 estavam conectados ao pipeline, mas partes das decisões dependiam de regex, nomes de arquivos e buscas textuais. Essas aproximações não distinguiam comentário de referência resolvida, overload de método, nulabilidade, constraint genérica ou uma relação real entre contrato, implementação e teste.

O AECS já produz um snapshot e um grafo Roslyn autenticados para a baseline. O candidato, porém, existe em um worktree descartável e precisa ser comparado sem mover o commit-base nem analisar arquivos fora da árvore staged.

## Decision

Derivar um segundo `RepositorySnapshot` de `git write-tree`, construir seu `CSharpSymbolGraph` e executar EB001–EB004 sobre um contrato comum que valida commit e hashes dos dois estados.

A estratégia `roslyn-msbuild-symbol-graph/v2` adiciona arestas `constructs` e fingerprints de constraints genéricas sem alterar o schema estrutural `aecs.csharp-symbol-graph/v1`. O store continua aceitando evidências históricas da estratégia v1, mas novos builders publicam v2.

Os verificadores limitam a origem da análise aos arquivos alterados, seguem relações resolvidas quando necessário, descontam violações estruturais preexistentes e anexam evidência versionada por finding. Falha de carga semântica em uma alteração C# retorna erro crítico; ausência ou erro de verificador obrigatório continua sendo rejeitada pelo `DecisionEngine`.

## Consequences

As decisões críticas deixam de tratar correspondência textual como autoridade e passam a explicar exatamente qual regra, símbolo, relação e baseline produziram o resultado. O custo é uma segunda carga Roslyn do candidato e maior sensibilidade à disponibilidade do SDK/MSBuild correto.

O grafo não modela intenção de negócio. A correlação modelo/migration permanece conservadora e a relação com testes depende de referências compiláveis. Heurísticas textuais podem continuar fora deste caminho, identificadas como probabilísticas e não bloqueantes por padrão.
