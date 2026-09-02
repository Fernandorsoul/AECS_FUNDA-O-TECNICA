# Adaptive Controller em shadow mode

O Adaptive Controller está conectado ao pipeline apenas como observador. O
`ExecutionController` fixo continua sendo a única autoridade para modelo, orçamento,
verificação e capabilities, e o `RepositoryContextCompiler` continua sendo a única
autoridade para o contexto efetivamente entregue ao agente.

## Fluxo e fonte de dados

Depois de resolver o plano fixo e capturar a baseline, o pipeline consulta no máximo 500
execuções do mesmo repositório. A consulta passa pelo `IEvidenceGraphSource`, que valida o
envelope, hash, assinatura, cadeia e escopo de repositório antes de expor um ID. O payload é
carregado e validado novamente pelo `IExecutionEvidenceStore`.

Somente execuções terminais, completas e reproduzíveis entram nas features. Registros sem
vínculo task/run, modelo, commit, hash do manifesto de contexto ou hash do candidato são
excluídos. Evidência adulterada, ilegível ou que muda entre a consulta e a carga também é
excluída e gera diagnóstico persistido. Dados em memória fornecidos pelo agente não fazem
parte desse caminho.

As features versionadas por `authenticated-risk-task-heuristic/v1` incluem:

- risco e tipo de tarefa determinístico;
- fingerprint SHA-256 do objetivo, sem persistir uma segunda cópia do texto;
- quantidades autenticadas, elegíveis, correspondentes e excluídas;
- IDs das evidências efetivamente usadas;
- taxa de sucesso recente e antiga para detecção de drift;
- modelo, estratégia de contexto, tokens, duração, custo contabilizado e decisão histórica.

## Fallbacks determinísticos

O controlador recomenda exatamente o plano fixo quando encontra `ColdStart`,
`InsufficientSample`, `DriftDetected`, `ContradictoryHistory` ou `InvalidHistory`. O estado,
as entradas e a justificativa ficam no `AdaptiveShadowEvidence` assinado.

Uma recomendação adaptativa exige pelo menos cinco execuções correspondentes por risco e
tipo. Modelos precisam de ao menos duas observações. Empates usam taxa de sucesso, custo
contabilizado e nome ordinal, nessa ordem. O orçamento recomendado usa o percentil 75 das
execuções verificadas com margem de 25%, sempre limitado componente a componente pelo plano
fixo. Capabilities e verificações são copiadas sem expansão.

Drift é sinalizado quando as metades antiga e recente têm diferença absoluta de taxa de
sucesso de pelo menos 0,50. Histórico contraditório é sinalizado quando o mesmo fingerprint
de objetivo e modelo possui resultados verificado e não verificado.

## Invariante de shadow mode

A recomendação nunca é aplicada. O pipeline envia ao agente o modelo do plano fixo, mantém o
orçamento do contrato, compila contexto pelo caminho normal e executa as capabilities
autorizadas pelo contrato. Uma recomendação que tenta ampliar orçamento ou capabilities é
descartada e substituída por `InvalidHistory`, sem interromper ou modificar a execução fixa.

Ao publicar, `aecs.adaptive-shadow/v1` registra estratégia, entradas, fontes, plano fixo,
recomendação, justificativa e avaliação contra o resultado real do controlador fixo. A
avaliação inclui decisão, estado, modelo e contexto executados, consumo, custo, duração e a
confirmação `counterfactualExecuted: false`. Ela não afirma qual seria o resultado causal de
um modelo não executado.

## Relatório offline

O relatório lê novamente apenas evidências autenticadas e agrupa por risco e tipo de tarefa:

```powershell
dotnet run --project src/AECS.Cli/AECS.Cli.csproj -- adaptive-report `
  --repo C:\repos\alvo `
  --format text `
  --evidence-store json
```

Use `--format json` para `aecs.adaptive-shadow-report/v1`, `--limit 1-500` para limitar a
janela e as mesmas opções de backend/keyring dos demais comandos. Cada grupo mostra número de
execuções, recomendações prontas, resultados verificados, concordâncias de modelo, duração e
custo contabilizado médio quando disponível. Registros legados sem shadow evidence e
registros inválidos aparecem como excluídos, nunca como observações implícitas.
