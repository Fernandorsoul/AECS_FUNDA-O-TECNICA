# ADR-013: Usar Roslyn/MSBuild como autoridade semântica para C#

- Status: Accepted
- Date: 2026-08-31

## Context

O indexador de contexto original extraía tipos, métodos e dependências com expressões regulares. Essa representação não distinguia overloads, partes de tipos parciais, records, genéricos, resolução de símbolos ou referências entre projetos e podia promover texto ambíguo a fato semântico.

O `RepositorySnapshot` já fornece uma baseline versionada, ordenada e endereçada por conteúdo, mas deliberadamente não carrega o modelo do compilador. O AECS precisa derivar fatos C# auditáveis sem ampliar o conjunto de arquivos autorizado pelo snapshot.

## Decision

Usar `MSBuildLocator`, `MSBuildWorkspace` e Roslyn para abrir soluções/projetos da baseline e produzir `aecs.csharp-symbol-graph/v1` com:

- nós de projeto, arquivo, namespace, tipo e membro;
- arestas explícitas de contenção, declaração, herança, implementação e referência;
- IDs e hashes estáveis, listas ordenadas e vínculo ao `RepositorySnapshot`;
- versões de compilador/MSBuild/SDK e opções efetivas persistidas;
- diagnósticos de carga e resolução, timeout, cancelamento e limites de volume;
- fallback textual restrito a inventário de arquivos, nunca usado como autoridade semântica.

O grafo válido alimenta o compilador de contexto, integra a evidência autenticada, o replay e a projeção do Evidence Graph. O replay trata qualquer hash semântico diferente como divergência de ambiente.

## Consequences

O contexto passa a reconhecer overloads, partials, records, genéricos, herança e chamadas entre projetos com a semântica real do compilador. O resultado é maior, depende de um SDK compatível e acrescenta custo ao preflight; falhas ficam explícitas e levam ao fallback semântico fail-closed.

`MSBuildWorkspace` executa avaliação de design time no processo do controlador. Propriedades fixas reduzem variabilidade, mas não constituem isolamento rígido de memória nem neutralizam todo comportamento de imports MSBuild. Análise de repositórios não confiáveis requer isolamento do controlador, e um worker dedicado com cotas de processo permanece como hardening futuro.
