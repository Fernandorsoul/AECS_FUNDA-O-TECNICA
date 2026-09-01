# Histórico durável e explicações do Jarvis

Os comandos `status`, `history`, `explain` e `context` do Jarvis consultam o mesmo store
operacional selecionado na CLI. Eles não dependem mais da memória da sessão e continuam úteis
depois de reiniciar o processo.

## Consultas

`status` retorna a execução autenticada mais recente do repositório. `history` aceita filtros
combináveis por tarefa, run e candidato, além da busca direta por evidence ID:

```text
aecs> status
aecs> status --json
aecs> history --task TASK-001 --limit 20
aecs> history --run <run-id> --json
aecs> history --candidate <candidate-id>
aecs> history --evidence <evidence-id>
```

`explain` e `context` aceitam o task ID na forma compatível ou um identificador explícito:

```text
aecs> explain TASK-001
aecs> explain --evidence <evidence-id> --json
aecs> explain --run <run-id>
aecs> context TASK-001
aecs> context --candidate <candidate-id> --format json
```

`--format text|json` e o atalho `--json` selecionam a apresentação. O limite permitido é
1–500. Uma busca por evidence ID é direta e não pode ser combinada com os demais IDs.

## Trust boundary da leitura

Toda listagem passa por `IEvidenceGraphSource`. JSON e PostgreSQL validam schema, assinatura,
hash e cadeia antes de projetar o registro. Em seguida, o payload é carregado novamente pelo
`IExecutionEvidenceStore`; um registro que desaparece ou muda entre as duas leituras é omitido
com diagnóstico.

O escopo usa o caminho canônico exato do repositório e o usuário do sistema operacional como
principal. Listagens não revelam registros de outro repositório, e uma leitura direta fora do
escopo é recusada. Evidência adulterada nunca é usada para compor uma explicação.

## Conteúdo e proveniência

O schema `aecs.jarvis-explanation/v1` organiza a resposta em três origens explícitas:

- `Persisted`: IDs de evidence/run/candidato, hashes, timestamps, autoridade da assinatura,
  decisão e motivo, gates, critérios de aceite, tentativas, budget, manifesto de contexto e
  promoções;
- `Derived`: somas e contagens calculadas somente a partir dos fatos persistidos, como tokens,
  arquivos alterados e gates aprovados/falhos/ausentes;
- `Interpretation`: resumo determinístico da decisão persistida, sem chamada a modelo.

A saída humana usa os prefixos `[persisted]`, `[derived]` e `[interpretation]`; a saída JSON
preserva os mesmos objetos e o campo `origin`.

Manifesto, modelo, estratégia, tokenizer, limites, arquivos, hashes e razões de seleção vêm da
evidência da execução. O comando `context` não recompila o repositório atual e não mostra um
preview inventado. Se a evidência autenticada for legada ou incompleta, o status é `Partial`,
os campos ausentes permanecem vazios e um diagnóstico é apresentado.
