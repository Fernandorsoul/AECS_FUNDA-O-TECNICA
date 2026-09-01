# Context Compiler orientado ao grafo

O `RepositoryContextCompiler` produz um pacote reproduzível de código a partir do worktree isolado da baseline. Ele não amplia o escopo do contrato e não usa conteúdo fora de `scope.allowed` para influenciar o prompt.

## Seleção e ranqueamento

O inventário começa apenas com arquivos C# autorizados e remove `scope.forbidden`. Cada arquivo recebe uma pontuação direta pelos termos do objetivo e dos critérios de aceite encontrados no caminho, nome e símbolos Roslyn, além de sinais de teste e correspondências exatas de escopo.

Quando há um `CSharpSymbolGraph` íntegro, os arquivos com relevância direta viram sementes de uma busca determinística. A busca inclui dependências e referências reversas entre arquivos, herança, implementação e partes de tipos parciais. Testes relacionados recebem bônus explícito. A profundidade padrão é 2; o menor caminho prevalece e a travessia ordenada impede ciclos de alterar o resultado. Sem uma semente textual, todos os arquivos autorizados viram sementes de fallback com a mesma pontuação mínima.

A ordenação final usa, nesta ordem: score decrescente, profundidade crescente e caminho ordinal. Arquivos autorizados mas desconectados permanecem no manifesto com profundidade `-1`, mesmo quando o orçamento não permite incluí-los.

## Tokenizers e orçamento

Cada adaptador fornece um `AgentContextProfile` com adaptador, modelo, tokenizer solicitado, janela de contexto, saída reservada e overhead do prompt sem contexto. O compilador resolve um `ITokenCounter` exato compatível quando registrado. Caso contrário, usa `utf8-byte-upper-bound@1`, em que cada byte UTF-8 conta como um token. Esse fallback superestima modelos usuais e evita o subdimensionamento de texto Unicode.

O teto efetivo é:

```text
min(limite configurado, budget.tokens, janela do modelo - saída reservada - overhead)
```

`ContextCompilationOptions` pode ser fornecido por compilação ou como padrão do `RepositoryContextCompiler` injetado no pipeline. Ele configura profundidade, teto total e limites por arquivo; o orçamento do contrato e o perfil do adaptador continuam sendo limites superiores irremovíveis.

O pacote completo, incluindo cabeçalhos e metadados, é medido pelo mesmo contador. O padrão adicional por arquivo é 4 mil tokens. Guardas de 48 mil caracteres totais e 16 mil caracteres por arquivo continuam limitando memória e volume. O truncamento preserva Unicode válido, acrescenta um marcador explícito e só é aceito se a nova medição permanecer dentro de todos os tetos. Se nem o cabeçalho mínimo couber, o compilador devolve prompt vazio em vez de exceder o orçamento.

Os adaptadores Ollama e cloud repetem a contagem conservadora e aplicam a mesma janela antes da chamada externa. A telemetria real do provedor continua sendo a autoridade posterior para consumo agregado.

## Manifesto e integridade

Compilações novas produzem `aecs.context-manifest/v2`. O manifesto contém:

- baseline, snapshot e grafo semântico utilizados;
- adaptador, modelo, tokenizer, janela, reservas e limites efetivos;
- estratégia e profundidade da expansão;
- uma decisão para cada arquivo elegível, com rank, score, relação, motivo e tokens;
- hashes SHA-256 do conteúdo original e do trecho incluído;
- arquivos incluídos com símbolos, tamanhos e estado de truncamento.

`ContextManifestFingerprint.Create` cobre todos esses campos. O ID deriva do fingerprint. Antes de assinar, persistir ou retornar evidência, o store valida fingerprint, contagens, caminhos, decisões, limites e vínculos com contrato, baseline, snapshot e grafo. Manifestos v1 autenticados permanecem legíveis para compatibilidade, sem receber atributos v2 retroativamente.

## Determinismo e segurança

Listas, relações e desempates usam ordem ordinal. O fingerprint não depende de timestamps, caminho absoluto ou ordem do sistema de arquivos. Caminhos são resolvidos novamente dentro do worktree, arquivos sensíveis são omitidos e nenhum candidato fora do escopo autorizado entra nas decisões ou no prompt.
