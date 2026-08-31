# CSharpSymbolGraph determinístico

O `CSharpSymbolGraph` transforma o inventário C# do `RepositorySnapshot` em fatos semânticos produzidos pelo compilador. Ele é construído na baseline isolada, antes de build, testes e chamada ao agente, e fica associado ao commit, ao snapshot e à versão efetiva das ferramentas.

## Contrato versionado

| Campo | Valor atual | Papel |
| --- | --- | --- |
| `schemaVersion` | `aecs.csharp-symbol-graph/v1` | formato persistido e validado no store |
| `strategyVersion` | `roslyn-msbuild-symbol-graph/v1` | estratégia de carga e extração semântica |
| `graphHash` | SHA-256 | endereço do grafo canônico ordenado |
| `repositorySnapshotHash` | SHA-256 | vínculo com o inventário que autorizou os arquivos |

O grafo também persiste commit-base, versões de Roslyn, MSBuild e SDK, propriedades globais, limites efetivos e diagnósticos. Uma versão futura que altere o significado dos fatos precisa publicar novo schema ou nova estratégia.

## Carga determinística

`RoslynSymbolGraphBuilder` usa `MSBuildLocator` e `MSBuildWorkspace`. As soluções `.sln`/`.slnx` do snapshot são abertas em ordem ordinal; projetos C# que não pertencem a uma solução são abertos depois, também em ordem. Somente documentos presentes no snapshot podem originar declarações.

A avaliação usa propriedades globais fixas de design time: configuração `Release`, plataforma `AnyCPU`, `BuildProjectReferences=false`, `DesignTimeBuild=true`, `SkipCompilerExecution=true`, exclusão explícita de `bin`/`obj` dos itens padrão e restore sem falhar por feeds indisponíveis. O estado de restore existente continua disponível em `obj`, mas novos artefatos intermediários do MSBuild são redirecionados para um diretório temporário isolado e removidos ao final, sem alterar o checkout analisado nem introduzir o caminho efêmero no hash. Parse options, compilation options, frameworks, símbolos de pré-processador e referências de projeto ficam registrados por projeto.

## Nós, arestas e identidade

O modelo representa:

- projetos, arquivos, namespaces, tipos e membros;
- classes, interfaces, structs, enums, delegates e records;
- overloads, genéricos e declarações parciais;
- contenção, declaração, herança, implementação e referências semânticas;
- referências entre projetos e alvos externos conhecidos pelo compilador.

IDs de projetos, nós, arestas e diagnósticos derivam de identidades canônicas. Hashes individuais cobrem os campos semânticos de cada elemento; `graphHash` cobre versões, opções, projetos, nós, arestas e diagnósticos ordenados. Overloads usam documentation IDs distintos, enquanto partes do mesmo tipo parcial convergem para um único nó com múltiplos caminhos.

## Diagnósticos e fallback

Falha ao localizar o SDK solicitado, abrir uma solução/projeto, resolver referência ou compilar semanticamente é registrada com origem, severidade, código, mensagem normalizada, projeto e posição segura. `LoadSucceeded=false` impede que o resultado seja tratado como autoridade semântica.

Nessa condição, o `CodebaseIndexer` fornece apenas um inventário textual de arquivos C#. Ele não cria tipos, membros, namespaces, heranças ou referências por regex. O manifesto marca `semanticIndex=textual-file-inventory` e não publica `symbolGraphHash`; fatos semânticos só vêm de um grafo Roslyn carregado com sucesso.

## Limites e cancelamento

Os limites v1 são persistidos junto do resultado: 90 segundos, 256 MiB de tamanho estimado determinístico, 128 projetos, 20 mil documentos, 200 mil nós, 1 milhão de arestas e 2 mil diagnósticos. O builder combina o token do pipeline com timeout próprio, verifica limites durante a travessia e falha fechado quando algum teto é excedido. Cancelamento solicitado pelo chamador é propagado; expiração interna é reportada como timeout.

O limite de memória é uma estimativa do payload semântico produzido, não um limite de working set do processo. A avaliação MSBuild de projetos ocorre no processo do controlador e pode interpretar imports e property functions do repositório. Ambientes que analisam código não confiável devem executar o controlador inteiro em isolamento adicional; mover a avaliação para um worker com cota rígida é uma evolução de hardening separada.

## Contexto, evidência e replay

Quando válido, o grafo alimenta a seleção de contexto e o manifesto incorpora seu hash. Esse vínculo participa do hash do manifesto, evitando que outro grafo seja associado silenciosamente ao mesmo contexto.

O envelope autenticado recalcula o hash do grafo e valida schema, estratégia, limites, caminhos, IDs, hashes e referências entre nós e arestas. O replay reconstrói o grafo no mesmo snapshot e compara hashes antes de continuar; divergência de compilador, MSBuild, SDK, opções, diagnósticos ou fatos é `EnvironmentDivergence`. Evidências legadas sem grafo continuam no fluxo compatível, sem receber fatos retroativos.

No Evidence Graph, `CSharpSymbolGraph` é um nó autenticado ligado ao snapshot por `derives-symbol-graph`, à baseline por `analyzed-by`, à execução por `records-symbol-graph` e ao contexto por `informs-context` quando o manifesto referencia exatamente o mesmo hash.
