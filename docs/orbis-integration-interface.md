# Interface de Integração AECS para Orbis

Este documento descreve a interface que o Orbis poderá consumir para integrar com o AECS.

## CLI Commands

O AECS CLI (`aecs`) expõe os seguintes comandos principais:

### 1. `run` - Execução única de tarefa

```bash
aecs run --repo <path> --task-file <path> [opções]
```

**Entrada:**
- `--repo`: Caminho do repositório Git
- `--task-file`: Caminho do arquivo YAML do contrato de tarefa
- Opções de runtime (ver abaixo)

**Saída:**
- Resultado da execução no stdout
- Evidência autenticada salva no store configurado
- Código de saída: 0 (sucesso), 1 (falha)

### 2. `experiment` - Execução em lote

```bash
aecs experiment --dataset <manifest.json> --output <directory> [opções]
```

### 3. `promote` - Promoção controlada

```bash
aecs promote --repo <path> --evidence <id> --diff-hash <sha256> --actor <actor> --confirm
```

### 4. `export-patch` - Exportação de patch

```bash
aecs export-patch --evidence <id> --diff-hash <sha256> --output <path> --actor <actor>
```

### 5. `evidence` - Consulta de evidências

```bash
aecs evidence <show|list|trace> --repo <path> [--evidence <id>] [--format <text|json|dot>]
```

### 6. `replay` - Replay de evidência

```bash
aecs replay --repo <path> --evidence <id>
```

### 7. `doctor` - Diagnóstico

```bash
aecs doctor [--repo <path>] [--format <text|json>]
```

### 8. `history` - Registro histórico

```bash
aecs history <ingest|review|suppress|list> [opções]
```

## Contrato de Tarefa (YAML)

O parser exige `schema_version` e a raiz `task`. Campos aceitam PascalCase ou snake_case.

```yaml
schema_version: "aecs.task-contract/v1"
task:
  id: "TASK-001"
  objective: "Corrigir bug no validador"
  acceptance:
    - "Todos os testes declarados passam"
  acceptance_evidence:
    - id: "AC-001"
      type: "verifier"
      reference: "Tests"
  scope:
    allowed:
      - "src/**"
    forbidden: []
  constraints:
    security_risk: "low"
    database_migration: false
    external_dependency: false
  budget:
    tokens: 10000
    usd: 1.0
    retries: 3
    wall_clock_seconds: 300
    max_files_changed: 5
  execution:
    working_directory: "."
    target: "MyApp.slnx"
    runtime: "docker"
  verification:
    build: "required"
    unit_tests: "required"
    scope: "required"
    security_scan: "optional"
  approval:
    production: "none"
```

## Evidência de Execução

A evidência é salva como JSON autenticado com:

- `id`: UUID da evidência
- `taskContract`: Contrato da tarefa
- `agentRun`: Execução do agente
- `agentResult`: Resultado do agente
- `baseline`: Snapshot da baseline
- `candidateChangeSet`: Mudanças produzidas
- `verificationResults`: Resultados dos verificadores
- `finalDecision`: Decisão final (Verified/Rejected/HumanReviewRequired)
- `stateTransitions`: Histórico de transições de estado
- `promotions`: Evidências de promoção

## Códigos de Saída

- `0`: Sucesso (Verified, Promoted, Exported)
- `1`: Falha (Rejected, Error, BudgetExceeded, etc.)

## Configuração

A configuração pode ser feita via:

1. Arquivo de configuração (`--runtime-config`)
2. Variáveis de ambiente
3. Flags da CLI

### Variáveis de ambiente principais:

- `AECS_AGENT_MODE`: Modo do agente (local/mock)
- `AECS_EVIDENCE_STORE`: Backend de evidências (json/postgres)
- `AECS_EVIDENCE_PATH`: Caminho das evidências JSON
- `AECS_EVIDENCE_KEY_DIRECTORY`: Diretório de chaves
- `AECS_POSTGRES_CONNECTION_STRING`: String de conexão PostgreSQL
- `OLLAMA_BASE_URL`: URL do Ollama
- `OPENAI_API_KEY`: Chave da API OpenAI/compatível

## Schemas Versionados

- `aecs.task-contract/v1`: Contrato de tarefa
- `aecs.runtime-config/v1`: Configuração de runtime
- `aecs.evidence-envelope/v1`: Envelope de evidência autenticado
- `aecs.experiment-report/v5`: Relatório de experimento

## Integração com Orbis

Para integrar o Orbis com o AECS:

1. **Backend C#/.NET**: Pode referenciar diretamente os assemblies do AECS
2. **Interface Angular**: Consome uma API do backend Orbis; somente o backend pode executar a CLI. O navegador nao executa subprocessos locais.
3. **Evidências**: O Orbis pode ler as evidências JSON diretamente do store
4. **Promoção**: O Orbis pode chamar `aecs promote` após aprovação humana

### Recomendação para o Orbis:

Criar uma API REST que encapsule a CLI do AECS, expondo:
- `POST /api/tasks` - Submeter tarefa
- `GET /api/tasks/{id}` - Status da execução
- `GET /api/evidence/{id}` - Consultar evidência
- `POST /api/evidence/{id}/promote` - Aprovar/promover
