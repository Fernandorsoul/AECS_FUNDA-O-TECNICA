# Experiment Harness reproduzível

O modo de dataset do comando `aecs experiment` executa uma matriz versionada de tarefas, variantes e repetições. Cada observação preserva a baseline Git resolvida, configuração solicitada, ambiente, resultado, falha individual e vínculo para a evidência autenticada da execução staged.

O modo legado `--repo/--tasks` continua disponível para execuções exploratórias de uma passagem. Experimentos comparáveis devem usar `--dataset`.

## Manifesto do dataset

O schema atual é `aecs.experiment-dataset/v1`. A identidade efetiva do dataset é o hash SHA-256 do manifesto mais a baseline resolvida. `HEAD` é aceito como referência portátil, mas é convertido para o commit exato antes do primeiro run; uma mudança posterior do HEAD invalida a retomada.

```json
{
  "schemaVersion": "aecs.experiment-dataset/v1",
  "id": "context-model-ab",
  "version": "1.0.0",
  "repository": {
    "path": "repository",
    "baseline": "HEAD"
  },
  "repetitions": 3,
  "referenceVariantId": "control",
  "tasks": [
    {
      "id": "TASK-001",
      "contractPath": "tasks/task-001.yaml",
      "expectedDecision": "Verified"
    }
  ],
  "variants": [
    {
      "id": "control",
      "provider": "Mock",
      "model": "mock-control-v1",
      "contextStrategy": "symbol-aware-control",
      "context": {
        "maxTokens": 12000,
        "maxCharacters": 48000,
        "maxFileCharacters": 16000,
        "maxFileTokens": 4000,
        "dependencyDepth": 2
      },
      "requiresRealProvider": false,
      "parameters": {}
    }
  ]
}
```

O caminho do repositório é relativo ao manifesto; contratos são relativos ao repositório. Caminhos absolutos, traversal, IDs duplicados, propriedades JSON duplicadas, schemas desconhecidos e combinações inválidas falham antes de executar.

Cada variante fixa:

- provider `Mock`, `Local` ou `Cloud`;
- identificador do modelo;
- nome e limites da estratégia de contexto;
- parâmetros suportados do provider;
- seed inicial, quando o provider a aceita.

Seeds de variantes `Local` e `Cloud` são enviados ao Ollama/OpenAI-compatible provider. A repetição `n` usa `seed + n - 1`. O mock é determinístico e rejeita seed. Parâmetros desconhecidos falham, evitando configurações registradas mas não aplicadas.

## Execução e isolamento

```powershell
aecs experiment `
  --dataset .\dataset.json `
  --output C:\aecs-results\context-model-ab `
  --evidence-root C:\aecs-evidence `
  --key-directory C:\aecs-keys
```

O diretório de saída deve ficar fora do repositório. Antes de cada repetição, o harness exige checkout limpo e o mesmo commit resolvido. O pipeline staged cria worktrees Git descartáveis e únicos para preflight/candidato; o resultado ainda exige `OriginalRepositoryUnchanged=true`. Assim, uma repetição nunca usa arquivos produzidos pela anterior.

Variantes `Local` e `Cloud` precisam declarar `requiresRealProvider=true` e são registradas como `Skipped` até que `--include-real-providers` seja passado. Chaves continuam externas ao manifesto. Parâmetros atuais:

| Provider | Parâmetros versionados |
| --- | --- |
| Mock | nenhum |
| Local | `baseUrl`, `contextWindowTokens` |
| Cloud | `baseUrl`, `contextWindowTokens`, `maxOutputTokens`, `temperature` |

## Checkpoints e retomada

Cada combinação `(datasetHash, task, variant, repetition)` recebe um `runKey` determinístico e um checkpoint imutável em `runs/<runKey>.json`. O checkpoint é gravado atomicamente somente depois do run terminar ou produzir uma falha/skip explícito.

```powershell
aecs experiment --dataset .\dataset.json --output C:\aecs-results\context-model-ab --resume
```

`--resume` carrega os mesmos run keys, não chama o provider novamente e não duplica resultados/evidências. Um output pertencente a outro hash de dataset é rejeitado. Sem `--resume`, reutilizar um output existente também é rejeitado.

## Relatórios

O output contém:

- `session.json`: hash do dataset e início da sessão;
- `runs/*.json`: checkpoints individuais, inclusive falhas e skips;
- `report.json`: relatório normalizado `aecs.experiment-report/v1`, ambiente e comparações pareadas;
- `results.csv`: uma linha por run, com modelo, contexto, seed, baseline, decisão e evidência;
- `comparisons.csv`: uma linha por par referência/candidato e repetição.

O pareamento é sempre feito dentro da mesma tarefa e repetição. Falha de uma variante não some da agregação: o par permanece com `bothCompleted=false` e a razão de cada lado. Runs concluídos registram `EvidenceId` e localização do envelope autenticado que originou os números.

## CI e providers reais

`tests/fixtures/experiment-smoke` é um dataset barato com provider mock, duas variantes e duas repetições. O CI inicializa sua baseline Git temporária, exige quatro checkpoints, valida JSON/CSV e repete o comando com `--resume` para provar idempotência. Datasets que dependem de Ollama ou cloud ficam fora desse smoke e só rodam com configuração explícita de provider/segredo.
