# Evidence Graph consultável

O Evidence Graph é uma projeção de leitura sobre os stores autenticados JSON e PostgreSQL. Ele não usa banco de grafo separado e não se torna uma segunda fonte de verdade: cada projeção só é criada depois da validação do schema, hashes, assinaturas, projeções relacionais e cadeia de eventos da evidência original.

## Comandos

Todas as consultas exigem um escopo explícito de repositório. O principal é obtido do usuário do sistema operacional que executa a CLI.

```powershell
# Resumo de uma execução
aecs evidence show --repo C:\repos\alvo --evidence <id>

# Lista e filtros combináveis
aecs evidence list --repo C:\repos\alvo --task TASK-001 --decision Verified
aecs evidence list --repo C:\repos\alvo --run <run-id>
aecs evidence list --repo C:\repos\alvo --candidate <candidate-id>
aecs evidence list --repo C:\repos\alvo --baseline <git-commit>
aecs evidence list --repo C:\repos\alvo --promotion <promotion-id>

# Traço causal legível
aecs evidence trace --repo C:\repos\alvo --evidence <id>
```

Acrescente `--evidence-store postgres` para o backend PostgreSQL ou `--evidence-root` para um diretório JSON não padrão. As opções de keyring são as mesmas dos outros consumidores de evidência.

## Exportação

`--format json` produz `aecs.evidence-graph/v1`, apropriado para processamento por máquina. `show` e `trace` também aceitam `--format dot`, que pode ser redirecionado para Graphviz:

```powershell
aecs evidence trace --repo C:\repos\alvo --evidence <id> --format json > graph.json
aecs evidence trace --repo C:\repos\alvo --evidence <id> --format dot > graph.dot
dot -Tsvg graph.dot -o graph.svg
```

Cada nó informa ID estável, tipo, origem, autoridade, timestamp, hashes, atributos e estado de validade. Arestas também têm ID estável, origem e autoridade. A projeção inclui repositório, tarefa, execução, run, tentativas, baseline, contexto, candidato, comandos, verificações, critérios de aceite, decisão, replays e promoções. Eventos posteriores preservam a ordem global assinada por arestas `authenticated-next`.

## IDs e ausência de inferência

IDs nativos (`Evidence`, `AgentRun`, tentativa, candidato, comando, verificação, replay e promoção) são preservados nos IDs do grafo. Nós sem ID nativo, como decisão, baseline, tarefa e critério, recebem IDs determinísticos derivados do repositório autenticado e de suas chaves explícitas. Repetir a consulta sobre o mesmo agregado produz os mesmos nós e arestas.

Uma referência inconsistente não gera uma aresta “provável”. A projeção registra um diagnóstico e omite a relação. Dados obrigatórios ausentes aparecem com `status=Missing`; registros cuja assinatura ou estrutura é inválida são omitidos da listagem com diagnóstico genérico, sem usar seus campos não confiáveis.

## Isolamento e autorização

O escopo de leitura é o caminho normalizado e exato que foi assinado na baseline. `list` não revela evidências de outros repositórios; `show` e `trace` recusam acesso cruzado. Um principal vazio, repositório inexistente ou store/keyring localizado dentro do repositório falha fechado.

Esse é o mecanismo local inicial de autorização. Em ambientes multiusuário, o processo da CLI deve executar sob uma identidade controlada e o acesso ao store, ao keyring e ao repositório deve ser limitado por ACL/secret manager. O escopo de aplicação não substitui autenticação do sistema operacional nem controle de acesso do PostgreSQL.
