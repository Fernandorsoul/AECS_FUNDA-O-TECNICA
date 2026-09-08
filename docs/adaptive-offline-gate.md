# Gate causal offline do Adaptive Controller

O comando `adaptive-experiment` avalia recomendações `Ready` do Adaptive Controller sem alterar o
fluxo operacional. Ele executa dois braços isolados para cada tarefa: o plano fixo autenticado e o
plano recomendado autenticado. O comando `run` continua aplicando somente o controlador fixo.

Esta infraestrutura implementa a issue #88. Ela não contém nem simula o corpus real: seleção,
pré-registro e execução das 50 tarefas pertencem à issue #89. A #35 permanece bloqueada até essa
coleta produzir uma decisão explícita.

## Contrato do dataset

`aecs.adaptive-offline-dataset/v1` é separado dos datasets H1 porque o tratamento é roteamento, não
apenas seleção de contexto. O JSON é estrito: campos desconhecidos, duplicados ou com casing
incorreto são rejeitados.

O manifesto exige:

- pelo menos 50 tarefas distintas, cada uma com contrato próprio;
- baseline Git completa e idêntica ao `HEAD` observado;
- data UTC de pré-registro e cutoff UTC por tarefa;
- origem HTTPS, licença SPDX e autorização de redistribuição do repositório;
- provider único (`Local` ou `Cloud`) e parâmetros congelados;
- evidência e hash autenticados da recomendação shadow de cada tarefa;
- hipótese H2, confiança de 95%, thresholds e política para referência zero.

Estrutura resumida; `tasks` precisa conter pelo menos 50 entradas reais:

```json
{
  "schemaVersion": "aecs.adaptive-offline-dataset/v1",
  "id": "adaptive-routing-real-corpus",
  "version": "1.0.0",
  "preregisteredAtUtc": "2026-09-03T00:00:00Z",
  "repository": {
    "path": "repository",
    "baseline": "0123456789abcdef0123456789abcdef01234567",
    "sourceUri": "https://example.org/owner/repository.git",
    "licenseSpdx": "MIT",
    "redistributionAllowed": true
  },
  "provider": {
    "kind": "Local",
    "parameters": {
      "baseUrl": "http://localhost:11434",
      "contextWindowTokens": "8192"
    }
  },
  "repetitions": 1,
  "protocol": {
    "design": "paired-adaptive-routing",
    "hypothesisId": "H2",
    "hypothesis": "Bounded adaptive routing improves VCC per effective cost.",
    "primaryMetric": "vcc-per-effective-cost",
    "minimumDistinctTasks": 50,
    "confidenceLevel": 0.95,
    "zeroReferencePolicy": "AbsolutePairedDelta",
    "deathCriteria": {
      "minimumRelativeImprovement": 0.1,
      "minimumAbsoluteImprovement": 1.0,
      "maximumCandidateFailureRate": 0.05,
      "maximumCandidateScopeViolationRate": 0.0,
      "maximumCandidateSecurityViolationRate": 0.0,
      "maximumMedianLatencyRegressionRate": 0.25
    }
  },
  "tasks": [
    {
      "id": "REAL-001",
      "contractPath": "tasks/REAL-001.yaml",
      "expectedDecision": "Verified",
      "seed": 7001,
      "historyCutoffUtc": "2026-09-02T12:00:00Z",
      "recommendationEvidenceId": "00000000-0000-0000-0000-000000000001",
      "recommendationEvidenceHash": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
    }
  ]
}
```

O exemplo é deliberadamente incompleto e não passa no loader até que as 50 entradas sejam
inventariadas.

## Preflight fail-closed

Antes de qualquer chamada ao provider, o gate:

1. autentica novamente a evidência da recomendação e confere seu hash;
2. exige recomendação `Ready`, `fixedPlanPreserved: true` e
   `counterfactualExecuted: false`;
3. confere risco, tipo e fingerprint do objetivo contra o TaskContract;
4. autentica cada `sourceEvidenceId` e exige pelo menos cinco fontes;
5. rejeita toda fonte no cutoff ou depois dele, incompleta ou não reproduzível;
6. exige capabilities e verificações idênticas e budget candidato limitado pelo fixo;
7. rejeita pares sem diferença material de modelo, contexto ou budget.

Falha de preflight gera checkpoint de par com falha e zero execução de provider. Depois da
execução, o runner também confere baseline, modelo, contexto, budget, evidência e preservação do
checkout original.

## Execução e retomada

O comando exige consentimento explícito para custo real:

```powershell
aecs adaptive-experiment `
  --dataset C:\aecs-adaptive\dataset.json `
  --output C:\aecs-adaptive-output `
  --include-real-providers `
  --allow-host-execution `
  --evidence-root C:\aecs-adaptive-evidence `
  --key-directory C:\aecs-adaptive-keys
```

Sem `--include-real-providers`, a execução é recusada. Docker continua sendo o runtime staged
padrão; `--allow-host-execution` permanece apenas como override explícito de desenvolvimento.

O output contém `session.json`, `pairs/<pairKey>.json` e
`aecs.adaptive-offline-report/v1` em `report.json`. Cada checkpoint tem fingerprint próprio e
inclui os IDs das evidências fonte e dos dois braços. `--resume` exige o mesmo hash de dataset e não
chama novamente o provider para pares já persistidos.

O processo retorna sucesso somente para conclusão `Maintain`. `Adjust` e `Abandon` retornam código
não zero sem apagar os artefatos.

## Interpretação

Pares ausentes continuam no denominador. A análise inclui VCC, first-pass, tokens, custo efetivo,
latência, retries e violações de scope/segurança. Custo ausente ou não positivo torna a métrica
primária indisponível e resulta em `Adjust`, nunca em custo zero implícito.

O relatório pode concluir `Maintain`, `Adjust` ou `Abandon`, mas não modifica feature flags nem
políticas. Mesmo um `Maintain` na #89 é somente evidência de entrada para a decisão humana e
arquitetural da #35.

## Opt-in operacional fail-closed

O runtime aceita uma política explícita de roteamento adaptativo para preparar a ativação gradual da
#35, mas o padrão continua sendo controlador fixo. Quando habilitada, a política só pode usar uma
recomendação shadow se todas as condições abaixo forem verdadeiras:

- `execution.adaptiveRouting.enabled` está `true`;
- `execution.adaptiveRouting.rollbackRequested` está ausente ou `false`;
- a recomendação autenticada está `Ready`;
- a quantidade de registros equivalentes é maior ou igual a `minimumReadyRecords` (padrão: 50);
- o risco da tarefa está em `allowedRisks` (padrão: `R0,R1`);
- o repositório corresponde ao canary configurado, quando `canaryRepositoryPath` é informado;
- capabilities, verificações e budget não são ampliados.

Nesta etapa, somente o modelo recomendado pode influenciar a execução. Budget, capabilities,
verificação e escopo continuam presos ao plano fixo/TaskContract. Qualquer violação preserva o
plano fixo ou falha fechada antes da chamada ao provider.

Exemplo de configuração para canary controlado:

```json
{
  "schemaVersion": "aecs.runtime-config/v1",
  "execution": {
    "adaptiveRouting": {
      "enabled": true,
      "minimumReadyRecords": 50,
      "allowedRisks": ["R0", "R1"],
      "canaryRepositoryPath": "C:\\repos\\produto-piloto"
    }
  }
}
```

Rollback operacional imediato:

```json
{
  "schemaVersion": "aecs.runtime-config/v1",
  "execution": {
    "adaptiveRouting": {
      "enabled": true,
      "rollbackRequested": true
    }
  }
}
```

As mesmas chaves podem ser sobrescritas por ambiente:

- `AECS_ADAPTIVE_ROUTING_ENABLED`
- `AECS_ADAPTIVE_ROUTING_ROLLBACK`
- `AECS_ADAPTIVE_ROUTING_MINIMUM_READY_RECORDS`
- `AECS_ADAPTIVE_ROUTING_ALLOWED_RISKS`
- `AECS_ADAPTIVE_ROUTING_CANARY_REPOSITORY`

Enquanto `AdaptiveShadowPlan` não carregar identidade própria de provider, cada dataset congela um
único provider para os dois braços. O gate não autoriza troca de provider em produção.

Consulte a [ADR-023](adr/ADR-023-paired-adaptive-offline-gate.md) para a decisão e seus trade-offs.

Para tarefas históricas com commits e oráculos diferentes, use
`aecs.adaptive-offline-dataset/v2`. O v2 mantém um checkout externo estável por baseline, valida
origem, licença, parent direto, diff test-only e imagem Docker por digest, e publica provenance por
par em `aecs.adaptive-offline-report/v2`. O procedimento está em
[Adaptação de corpus real](adaptive-real-corpus.md) e na
[ADR-024](adr/ADR-024-multi-baseline-real-corpus.md).
