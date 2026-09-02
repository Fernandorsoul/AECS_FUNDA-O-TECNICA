# Adaptação de corpus real para o gate adaptativo

O schema `aecs.adaptive-offline-dataset/v2` permite avaliar tarefas históricas que possuem commits e
oráculos diferentes. Ele não baixa benchmarks, não cria gold patches e não incorpora repositórios
externos ao AECS.

## Layout externo

Mantenha o pacote operacional fora do checkout do AECS:

```text
C:\aecs-adaptive-corpus\
  dataset.json
  contracts\
    REAL-001.yaml
    ...
  checkouts\
    REAL-001\
    ...
C:\aecs-adaptive-output\
C:\aecs-adaptive-evidence\
C:\aecs-adaptive-keys\
```

Cada checkout precisa permanecer no mesmo path desde a geração da recomendação shadow até o fim do
experimento. Checkouts de tarefas com baselines diferentes não podem compartilhar o mesmo diretório.

## Preparação de uma instância

1. Verifique a licença do repositório e os termos do dataset original.
2. Crie um checkout limpo no `upstreamCommit`, anterior à solução.
3. Valide externamente que o teste fail-to-pass falha e que os testes pass-to-pass passam.
4. Aplique somente o test/oracle patch autorizado e crie um único commit filho.
5. Calcule o hash de `git diff --binary --no-ext-diff <upstream> <baseline> --`.
6. Fixe uma imagem Docker por digest e configure o mesmo valor no TaskContract.
7. Declare os paths tocados pelo oráculo em `oracle.allowedPaths` e também em
   `task.scope.forbidden`.
8. Remova gold/solution patches e qualquer arquivo não necessário antes do pré-registro.
9. Execute o AECS uma vez em shadow mode para produzir a recomendação autenticada da tarefa.
10. Registre os IDs/hashes da recomendação e das fontes, então congele o dataset antes da primeira
    execução pareada.

O gate exige que `baselineCommit` tenha exatamente um parent e que ele seja `upstreamCommit`. O
remote `origin`, o `HEAD`, o diff do oráculo e o status limpo são recalculados antes do provider.

## Estrutura v2 resumida

```json
{
  "schemaVersion": "aecs.adaptive-offline-dataset/v2",
  "id": "adaptive-real-corpus",
  "version": "2.0.0",
  "preregisteredAtUtc": "2026-09-05T00:00:00Z",
  "repositories": [
    {
      "id": "real-001-checkout",
      "path": "checkouts/REAL-001",
      "sourceUri": "https://github.com/owner/repository.git",
      "licenseSpdx": "MIT",
      "redistributionAllowed": true
    }
  ],
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
      "contractPath": "contracts/REAL-001.yaml",
      "contractHash": "sha256:5555555555555555555555555555555555555555555555555555555555555555",
      "expectedDecision": "Verified",
      "seed": 9001,
      "historyCutoffUtc": "2026-09-03T00:00:00Z",
      "recommendationEvidenceId": "00000000-0000-0000-0000-000000000001",
      "recommendationEvidenceHash": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
      "repositoryId": "real-001-checkout",
      "upstreamCommit": "1111111111111111111111111111111111111111",
      "baselineCommit": "2222222222222222222222222222222222222222",
      "oracle": {
        "kind": "PrecommittedTestPatch",
        "diffHash": "sha256:3333333333333333333333333333333333333333333333333333333333333333",
        "allowedPaths": ["tests/**"],
        "containerImage": "registry.example/image@sha256:4444444444444444444444444444444444444444444444444444444444444444",
        "failToPass": ["test_regression"],
        "passToPass": ["test_existing"]
      }
    }
  ]
}
```

O exemplo é incompleto: o loader exige pelo menos 50 tarefas, todas as entradas do catálogo devem
ser usadas e cada recomendação deve ter ID exclusivo.

## Invariantes fail-closed

- propriedades desconhecidas, duplicadas, casing incorreto e enums numéricos são rejeitados;
- paths do dataset não podem escapar a raiz do manifesto;
- o conteúdo de cada TaskContract precisa corresponder ao hash pré-registrado;
- origem precisa ser HTTP(S), licença precisa ser explícita e o uso deve estar autorizado;
- checkouts precisam estar limpos e no `baselineCommit` exato;
- `baselineCommit` precisa ser filho direto de `upstreamCommit`;
- o hash e os paths efetivos do oráculo precisam corresponder ao manifesto;
- imagem do oráculo e imagem Docker do contrato precisam ser idênticas por digest;
- paths do oráculo precisam estar proibidos ao agente;
- recomendação e fontes continuam sujeitas ao cutoff e à autenticação do protocolo v1;
- qualquer falha anterior ao provider gera par com falha e permanece no denominador.

Não existe campo `goldPatch` no schema. Adicioná-lo causa rejeição do JSON. O patch de solução pode
ser usado apenas fora do AECS para validar a curadoria e deve ser removido antes do pré-registro.

## Seleção do corpus da #89

O v2 remove a limitação técnica, mas não seleciona automaticamente o benchmark. Antes de escolher,
compare pelo menos:

- representatividade de tarefas de repositório para o AECS;
- oráculos reproduzíveis e tempo de build/teste;
- linguagens cobertas pelo Context Compiler atual;
- licença por repositório e permissão de redistribuição;
- tamanho das imagens e margem de RAM/disco no hardware alvo;
- risco de contaminação do modelo pelos issues e patches públicos.

[SWE-bench](https://github.com/SWE-bench/SWE-bench/blob/main/docs/guides/datasets.md) oferece tarefas
reais e oráculos executáveis, mas exige adaptação multi-repositório e não inclui C# no
[conjunto Multilingual](https://www.swebench.com/multilingual.html).
[InferredBugs](https://github.com/microsoft/InferredBugs) é útil para pesquisa C#, porém não fornece
sozinho o ambiente executável necessário para VCC.
[RunBugRun](https://github.com/giganticode/run_bug_run) possui C# executável, mas não representa uma
codebase de repositório e requer auditoria das licenças de origem.

Consulte a [ADR-024](adr/ADR-024-multi-baseline-real-corpus.md). A decisão de corpus e a execução das
50 tarefas permanecem na #89; a #35 continua bloqueada.
