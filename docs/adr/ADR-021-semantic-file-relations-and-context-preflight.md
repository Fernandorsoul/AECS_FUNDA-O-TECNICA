# ADR-021: Restringir relações semânticas entre arquivos e validar contexto antes do provider

- Status: Accepted
- Date: 2026-09-02

## Context

A estratégia `graph-ranked-token-budget/v1` projetava toda aresta do grafo Roslyn como relação
entre arquivos. Arestas estruturais como `contains` e `declares` atravessavam namespaces e projetos,
fazendo tipos sem dependência semântica parecerem relacionados. Além disso, qualquer nó presente em
mais de um arquivo era projetado como `partial`, inclusive namespaces.

No benchmark H1, essas relações elevaram os oito arquivos `AaaDecoys` acima dos arquivos `Policy` e
`Contract`. Com o orçamento registrado de 1600 tokens, a variante graph-ranked incluiu apenas dois
decoys nos 30 runs locais e omitiu os arquivos necessários. O teste anterior compilava a fixture sem
o grafo Roslyn real e, portanto, não exercitava a projeção defeituosa.

O pipeline, a autenticação e os verificadores funcionaram, mas os resultados de qualidade de #74 e
#77 não isolam capacidade do modelo de qualidade do contexto. Também faltava um gate que impedisse
um experimento pré-registrado de chamar o provider quando seu contexto candidato não contivesse os
arquivos relevantes conhecidos pela fixture.

## Decision

- Substituir `graph-ranked-token-budget/v1` por `graph-ranked-token-budget/v2` para novas execuções.
- Projetar entre arquivos somente arestas Roslyn de dependência semântica explicitamente permitidas:
  `references`, `constructs`, `inherits` e `implements`, além das respectivas relações reversas.
- Não projetar `contains`, `declares` ou outras arestas estruturais como dependências entre arquivos.
- Criar relação `partial` somente para nós `type` que possuam o modificador `partial` e mais de um
  arquivo de declaração.
- Continuar aceitando e autenticando manifestos históricos v1; replays não recalculam sua seleção
  com a estratégia v2.
- Introduzir `aecs.experiment-dataset/v3`. Em A/Bs de contexto, cada tarefa declara
  `requiredContextPaths`, e um gate aplicado imediatamente após a compilação do contexto rejeita o
  run antes da chamada ao provider quando qualquer caminho obrigatório estiver ausente.
- Registrar os caminhos e hashes de contexto efetivamente incluídos nos resultados e comparações
  pareadas, para distinguir mudança de configuração de contraste material.
- Preservar os artefatos publicados de #74 e #77 e adicionar errata; não reescrever evidência
  histórica nem atribuir retrospectivamente um resultado ao modelo.

## Consequences

Arquivos irmãos em um namespace deixam de receber score de dependência semântica. Referências,
construções, herança, implementação e partes realmente parciais continuam navegáveis e auditáveis.
Os fingerprints de novas compilações mudam de forma explícita, enquanto evidências v1 permanecem
verificáveis.

Datasets v1 e v2 históricos continuam legíveis. Novos A/Bs de contexto usam v3 e precisam declarar
o oráculo de relevância da fixture. Esse oráculo é controle experimental, não uma heurística de
produção: ele prova que o experimento mediu o contraste pretendido, mas não escolhe arquivos para
tarefas reais.

O H1 local precisa ser repetido depois da correção. Até lá, ele comprova o caminho operacional do
provider local e a rejeição governada, mas não mede a eficácia do Context Compiler nem a capacidade
intrínseca dos modelos 1.5B e 3B sob contexto correto.
