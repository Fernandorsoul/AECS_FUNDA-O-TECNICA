# Experiment Harness reproduzível

O modo de dataset do comando `aecs experiment` executa uma matriz versionada de tarefas, variantes e repetições. Cada observação preserva a baseline Git resolvida, configuração solicitada, ambiente, resultado, falha individual e vínculo para a evidência autenticada da execução staged.

O modo legado `--repo/--tasks` continua disponível para execuções exploratórias de uma passagem. Experimentos comparáveis devem usar `--dataset`.

## Manifesto do dataset

O schema geral é `aecs.experiment-dataset/v1`; o contrato controlado de contexto A/B usa
v2/v3 e a política explícita de decisão para novos experimentos usa
`aecs.experiment-dataset/v4`. Datasets A/B v2/v3 permanecem legíveis como evidência histórica. A
identidade efetiva do dataset é o hash SHA-256 do manifesto
mais a baseline resolvida. `HEAD` é aceito como referência portátil, mas é convertido para o
commit exato antes do primeiro run; uma mudança posterior do HEAD invalida a retomada.

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
      "contextStrategy": "graph-ranked",
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

Em v1, nomes históricos de estratégia continuam significando o compilador orientado ao grafo.
Em v2, v3 e v4, somente `naive-path-order` e `graph-ranked` são aceitos. A primeira variante ignora
objetivo, símbolos e relações e inclui arquivos exclusivamente pela ordem ordinal dos caminhos;
a segunda usa o ranqueamento semântico normal. Em v3/v4, cada tarefa também declara
`requiredContextPaths`. A variante graph-ranked é rejeitada depois da compilação do contexto e
antes da chamada ao provider se qualquer caminho obrigatório estiver ausente.

## Harness Manifest (P4)

Cada run de variante registra agora um **Harness Manifest** versionado (`aecs.harness/v1`) e
imutável — identidade por hash canônico SHA-256 do conteúdo (`ManifestHash`):

- Derivado automaticamente da definição real da variante por
  `HarnessVariantBinding.ForVariant` (`src/AECS.Application/Experiments/HarnessVariantBinding.cs`);
  gravado em `TaskExperimentResult` como `HarnessManifestHash`, `HarnessVariantId` e
  `HarnessSecurityBaseline` (omitidos em runs legados sem variante).
- Declara os 5 módulos do harness: `agent-loop`, `context`, `observation`, `tool-use`,
  `completion`. No protocolo atual só o módulo `context` varia (`naive-path-order` vs
  `graph-ranked`); os demais ficam em `baseline` / `preauthorized` /
  `required-verifiers`.
- `tool-use.policyRef` deve ser `immutable-approved-policy`; `completion.strategy` deve ser
  `required-verifiers` — gates de verificação **não** são variáveis do harness.
- `SecurityBaseline` fixa `aecs.immutable-gates/v1` (AgentSuccess, Application,
  NonEmptyChange, Scope, Budget, Build, Tests, SecurityScan, EB001, AcceptanceCriteria,
  **ConstraintLedger**) — variantes diferentes compartilham a mesma baseline de gates.
- `HarnessManifestContract.Validate` rejeita: módulo desconhecido/duplicado, estratégia fora
  do allowlist, parâmetro fora do allowlist (whitelist: só `max_tokens` e `dependency_depth`
  no módulo `context`), `policyRef` divergente e hash adulterado.
- Duas variantes do protocolo A/B produzem hashes distintos e reprodutíveis
  (`tests/AECS.UnitTests/HarnessManifestTests.cs`).
- Auto-modificação permanece fora de escopo: não existe variante cujo candidato altere
  políticas de segurança, isolamento, critérios obrigatórios de aprovação ou sua própria
  avaliação (plano §5.2/§5.4).

## Protocolo fatorial 2×2 — Ledger × Context (P5)

Schema `aecs.experiment-dataset/v5`, design `factorial-ledger-context-2x2` (plano §6). Quatro
braços pré-registrados, fatores constantes (provider/modelo/seed/context-limits/parâmetros
idênticos):

| Braço | `constraintLedger` | `contextStrategy` |
|---|---|---|
| A (referência) | false | naive-path-order |
| B | true | naive-path-order |
| C | false | graph-ranked |
| D | true | graph-ranked |

- O loader **rejeita** qualquer atribuição de braço diferente da tabela, dataset sem os 4 ids
  A–D ou protocolo divergente (hipótese/métrica/mínimos/limites como no A/B pareado).
- Cada variante carrega `constraintLedger` (default `true`); o CLI materializa pipelines por
  variante (`ExperimentRunner(Func<ExperimentVariantDefinition, StagedExecutionPipeline>)`),
  e `StagedExecutionPipeline(constraintLedgerEnabled: false)` executa o arm **sem** projetar
  o ledger, sem gate `ConstraintLedger`, sem `ConstraintSet` na evidência e sem gravação no
  ledger store — a avaliação externa (trust-boundary, scope, build) permanece idêntica.
- Todo run registra contagens do ledger (`constraintActiveCount`, `constraintSatisfiedCount`,
  `constraintViolatedCount`, `constraintPendingReviewCount`) e `constraintLedgerEnabled`.
- O relatório ganha `factorial` com: `armSummaries` (incluindo CRR e cobertura de verificação;
  denominador zero ⇒ `null`/N/D), contrates pareados `ledger-effect-baseline` (B−A),
  `ledger-effect-graph-ranked` (D−C), `harness-effect-ledger-off` (C−A),
  `harness-effect-ledger-on` (D−B) e `interaction` ((D−C)−(B−A)), além de
  `falseBlockCandidates` (ledger-on rejeitou com zero violações onde ledger-off verificou).
- `Limitations` sempre declara: protocolo é validação de método — resultados com mock **não**
  demonstram ganho; pending nunca conta como satisfied; métricas com denominador zero são N/D.
- Fixture mock: `tests/fixtures/experiment-factorial` (2 tarefas × 4 braços × 1 repetição =
  8 runs), executado no CI; testes unitários cobrem aceitação/rejeição do loader e a
  semântica dos contrastes (`tests/AECS.UnitTests/ExperimentFactorialTests.cs`).

## Protocolo A/B pré-registrado

Um manifesto v2, v3 ou v4 exige exatamente duas variantes e o bloco `protocol`. O loader comprova antes
da primeira chamada que provider, modelo, seed, parâmetros e todos os limites são idênticos.
A referência deve ser `naive-path-order`, a candidata `graph-ranked`, e
`tasks × repetitions` deve atingir `minimumPairedSamples`.

O protocolo registra H1, métrica primária `vcc-per-estimated-cost`, confiança de 95%, efeito
mínimo, taxa máxima de falhas e taxa máxima de violações de escopo. O benchmark canônico fica
em [`experiments/context-compiler-h1`](../experiments/context-compiler-h1/README.md), com 30
pares e provider real opt-in; ele não roda no smoke barato do CI.

Em v2/v3, referência com custo válido e zero VCC preserva a regra histórica: a melhora relativa é
indefinida e a conclusão é `Adjust`, com uma razão específica. Isso evita reinterpretar um dataset
depois de observar seu resultado. Um novo dataset v4 deve pré-registrar `zeroReferencePolicy`:

```json
{
  "schemaVersion": "aecs.experiment-dataset/v4",
  "protocol": {
    "zeroReferencePolicy": "AbsolutePairedDelta",
    "deathCriteria": {
      "minimumRelativeImprovement": 0.05,
      "minimumAbsoluteImprovement": 5.0,
      "maximumCandidateFailureRate": 0.20,
      "maximumCandidateScopeViolationRate": 0.00
    }
  }
}
```

`Adjust` é a alternativa conservadora e não aceita limiar absoluto. `AbsolutePairedDelta` exige
`minimumAbsoluteImprovement` nas mesmas unidades de VCC por custo estimado do par. Nesse caminho,
`Maintain` requer que a média alcance o limiar e que o limite inferior do IC pareado de 95% fique
estritamente acima dele. Configuração ausente, enum desconhecido, limiar negativo/não finito ou
campos de política em schemas anteriores falham fechado. A escolha integra o hash do dataset.

Seeds de variantes `Local` e `Cloud` são enviados ao Ollama/OpenAI-compatible provider. A repetição `n` usa `seed + n - 1`. O mock é determinístico e rejeita seed. Parâmetros desconhecidos falham, evitando configurações registradas mas não aplicadas.

## Execução e isolamento

```powershell
aecs experiment `
  --dataset .\dataset.json `
  --output C:\aecs-results\context-model-ab `
  --evidence-root C:\aecs-evidence `
  --key-directory C:\aecs-keys
```

Um ledger posterior de cobrança pode ser aplicado com
`--cost-reconciliation <ledger.json>`, inclusive junto de `--resume`, sem executar novamente o
provider. O formato e as validações estão em [Contabilidade de custo, VCC e CPVC](cost-accounting.md).

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
- `report.json`: relatório normalizado `aecs.experiment-report/v5`, ambiente, regra de decisão aplicada, análise, custo, contexto efetivo e comparações pareadas;
- `results.csv`: uma linha por run, com modelo, caminhos/hashes do contexto, seed, baseline, decisão e evidência;
- `comparisons.csv`: uma linha por par e repetição, com deltas de VCC, first-pass, tokens,
  custo estimado, latência, escopo, rework e contraste material do contexto;
- `analysis.csv`: uma linha por variante com amostra, taxas, custo e intervalos de confiança;
- `cost-records.csv`: uso estimado/final, divergências, custo estimado/reconciliado e vínculo de
  evidência por run;
- `cost-efficiency.csv`: CPVC e incerteza geral, por modelo, risco, tarefa, estratégia e período.

O pareamento é sempre feito dentro da mesma tarefa e repetição. Falha de uma variante não some da agregação: o par permanece com `bothCompleted=false` e a razão de cada lado. Runs concluídos registram `EvidenceId` e localização do envelope autenticado que originou os números.

VCC segue `aecs.vcc/v1`: decisão `Verified`, execução concluída, mudança Git não vazia, checkout
original intacto, nenhuma violação de escopo e evidência de origem persistida. First-pass exige também zero retry.
`rework` é declarado estritamente como novas tentativas do agente dentro do run; não representa
rework pós-merge. O custo efetivo prefere reconciliação e mantém estimativas explicitamente
rotuladas. Custo ausente ou nenhum VCC deixa CPVC indisponível, nunca zero. Consulte a
[especificação completa](cost-accounting.md).

Para cada distribuição, o relatório preserva tamanho, mínimo, quartis, média, mediana, máximo,
desvio-padrão e intervalo de confiança de 95% da média. A conclusão automática é `Maintain`
somente quando o efeito mínimo é atingido e o intervalo pareado exclui zero; evidência faltante
ou incerta produz `Adjust`; um critério de morte atingido produz `Abandon`. O relatório e
`analysis.csv` registram a política para referência zero, se ela foi pré-registrada, se o caso foi
observado, a métrica efetiva e o limiar aplicado.

## CI e providers reais

`tests/fixtures/experiment-smoke` é um dataset barato com provider mock, duas variantes e duas repetições. O CI inicializa sua baseline Git temporária, exige quatro checkpoints, valida JSON/CSV e repete o comando com `--resume` para provar idempotência. Datasets que dependem de Ollama ou cloud ficam fora desse smoke e só rodam com configuração explícita de provider/segredo.
