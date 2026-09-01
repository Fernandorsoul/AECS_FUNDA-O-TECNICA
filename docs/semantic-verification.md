# Verificação semântica EB001–EB004

Os verificadores EB001–EB004 comparam dois estados reais do compilador: a baseline autenticada e o candidato materializado a partir do índice Git do worktree descartável. Texto que apenas se parece com código não é promovido a fato semântico.

## Fluxo executado

1. O preflight cria o `RepositorySnapshot` da árvore da baseline e o respectivo `CSharpSymbolGraph` com Roslyn/MSBuild.
2. Depois da alteração do agente, o pipeline executa `git add -A` no worktree descartável e `git write-tree`. Essa árvore, sem mover o commit da baseline, origina o snapshot exato do candidato.
3. O mesmo builder Roslyn cria o grafo do candidato. Os dois grafos carregam projetos, namespaces, tipos, membros e relações resolvidas, incluindo `project-reference`, `references`, `inherits`, `implements` e `constructs`.
4. `SemanticAnalysisInput` rejeita combinações de commit, snapshot e grafo incompatíveis. Quando arquivos C#, projetos ou configuração MSBuild foram alterados, `LoadSucceeded=false` produz `VerificationStatus.Error` com severidade crítica.
5. Cada regra examina somente símbolos do candidato declarados em arquivos alterados e, quando necessário, as relações reais que chegam ou saem desses símbolos. Violações que já existiam na baseline são descontadas em EB001 e EB002.

O replay repete a criação do snapshot e do grafo do candidato antes de executar os mesmos verificadores. Evidência legada sem a autoridade necessária não recebe um resultado otimista: o verificador retorna erro.

## Regras atuais em código

| Verificador | Autoridade e comparação | Resultado bloqueante |
| --- | --- | --- |
| EB001 | Arestas resolvidas entre projetos, namespaces e símbolos são comparadas com regras configuráveis de origem e dependência proibida. Somente relações novas no candidato viram findings. | Violações retornam `Fail`; as regras críticas também são bloqueadas pela política semântica padrão. |
| EB002 | Regras configuráveis consultam `TypeKind`, nomes de símbolos, modificador `async`, arestas `implements` e criações de objeto resolvidas. | Findings `Error` retornam `Fail`; `Info` e `Warning` permanecem evidência com `Pass`. |
| EB003 | A API pública da baseline é identificada por projeto e documentation ID. A assinatura compara display semântico com nulabilidade, acessibilidade, tipo de símbolo, overload/arity e constraints genéricas. | Remoção ou alteração de assinatura retorna `Fail/Critical`. |
| EB004 | Implementações vêm de `implements`; testes vêm de referências reversas originadas em projetos de teste; entidades vêm de referências do `DbContext`; migrations e snapshots são tipos derivados resolvidos. | Contrato sem mudança correlata na implementação retorna `Fail/Error`; testes e migrations ausentes são `Warning/Pass`. |

EB004 não afirma que toda alteração de modelo exige migration. Ele só emite a heurística quando o tipo impactado é uma entidade realmente referenciada por um `DbContext` e o mesmo projeto já contém tipos `Migration` ou `ModelSnapshot` resolvidos.

## Evidência explicável

Cada `VerificationResult` de EB001–EB004 inclui `SemanticVerificationEvidence` com:

- commit da baseline;
- hashes dos snapshots e grafos da baseline e do candidato;
- conjunto completo de arquivos impactados;
- para cada finding: regra, ID e nome do símbolo, localização, severidade, baseline e justificativa.

O store autenticado valida schema, hashes, vínculo com a baseline, caminhos impactados e campos dos findings antes de assinar ou aceitar a evidência. A CLI mostra os hashes e até cinco findings por verificador.

## Regex e fallback textual

Regex e busca textual não participam da decisão de EB001–EB004. O campo legado `PatternRule.Pattern` é mantido apenas para compatibilidade e não é consumido pelo verificador. O fallback do indexador só inventaria arquivos; se uma alteração C# exige autoridade semântica, ele falha fechado.

EB005 é um verificador histórico separado. Desde o registro versionado, ele também usa somente seletores de símbolos e relações resolvidas; extrações heurísticas permanecem em draft até revisão, e enforcement bloqueante exige aprovação humana. Consulte [Registro histórico do EB005](historical-decision-registry.md).

## Medição das fixtures

A matriz abaixo é executada em `AECS.UnitTests` com grafos determinísticos pequenos. “Positiva” significa que o finding deve existir; “negativa” significa que ele não deve existir.

| Verificador | Fixtures positivas | Fixtures negativas | TP | FP | FN | TN |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| EB001 | 2 | 2 | 2 | 0 | 0 | 2 |
| EB002 | 3 | 2 | 3 | 0 | 0 | 2 |
| EB003 | 3 | 2 | 3 | 0 | 0 | 2 |
| EB004 | 3 | 2 | 3 | 0 | 0 | 2 |
| Total | 11 | 8 | 11 | 0 | 0 | 8 |

As positivas cobrem dependência proibida resolvida, referência entre projetos, interface ausente, criação direta, async, remoção de overload, nulabilidade, constraint genérica e relações de implementação/teste/migration. As negativas cobrem dívida preexistente, texto sem aresta, implementação válida, criação apenas mencionada em string, API movida sem alteração semântica, membro privado, mudança correlata e arquivo com “Test” no nome sem relação resolvida.

Essa matriz mede regressão e confusão nas fixtures conhecidas; não é uma estimativa estatística de precisão em repositórios desconhecidos. Integração adicional cobre a árvore staged do candidato e a extração Roslyn real de `constructs`, nulabilidade e constraints.
