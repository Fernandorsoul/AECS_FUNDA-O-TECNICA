# Referência do TaskContract

O `TaskContract` é a unidade de trabalho do AECS. Ele define o resultado esperado, quais arquivos podem mudar, quanto a execução pode consumir, quais verificações são obrigatórias e se a entrega exige aprovação humana.

## Exemplo completo

```yaml
task:
  id: TASK-001
  objective: Fix NullReferenceException in CustomerMapper when input is null
  acceptance:
    - No NullReferenceException when Customer is null
    - Existing tests still pass
    - Null input returns an empty result
  acceptance_evidence:
    - id: AC-001
      type: test
      reference: FullyQualifiedName~CustomerMapperTests.NullCustomer
      test_path: tests/Customers/CustomerMapperTests.cs
      behavioral: true
    - id: AC-002
      type: verifier
      reference: Tests
    - id: AC-003
      type: test
      reference: FullyQualifiedName~CustomerMapperTests.NullCustomer
      test_path: tests/Customers/CustomerMapperTests.cs
      behavioral: true
  scope:
    allowed:
      - src/Customers/**
      - tests/Customers/**
    forbidden:
      - src/Billing/**
      - src/Orders/**
  constraints:
    security_risk: low
    database_migration: false
    external_dependency: false
  budget:
    tokens: 60000
    usd: 0.20
    retries: 1
    wall_clock_seconds: 120
    max_files_changed: 5
  execution:
    runtime: docker
    working_directory: .
    target: SampleProject.slnx
    sandbox:
      image: mcr.microsoft.com/dotnet/sdk:9.0@sha256:f190d2dd9eef2899c91ac323caa0bd2b39334a5400ba93013e5199da39dad940
      cpu_limit: "1.0"
      memory_limit: 512m
      process_limit: 128
      wall_clock_seconds: 120
      network_access: false
  verification:
    build: required
    unit_tests: required
    integration_tests: optional
    scope: required
    security_scan: optional
    architecture: optional
    critical_semantic_failures: required
    required_semantic_verifiers:
      - EB003-BreakingChange
  approval:
    production: none
```

O parser aceita a raiz `task` ou `Task` e propriedades em snake_case ou PascalCase. Campos desconhecidos são ignorados; por isso, valide alterações de schema com testes para evitar erros de digitação silenciosos.

## Campos

### Identidade e objetivo

| Campo | Tipo | Padrão | Semântica |
| --- | --- | --- | --- |
| `id` | string | vazio | Identificador usado em relatórios e histórico |
| `objective` | string | vazio | Instrução principal enviada ao agente e usada na classificação de risco |
| `acceptance` | lista de strings | vazia | Resultados observáveis que devem orientar geração e verificação |
| `acceptance_evidence` | lista de associações | vazia | Liga cada critério a um verificador ou teste executável |

O parser ainda não rejeita `id` ou `objective` vazios. Trate ambos como obrigatórios ao escrever novos contratos.

Cada item de `acceptance` recebe um identificador determinístico na ordem declarada (`AC-001`, `AC-002`, ...). Uma associação de `acceptance_evidence` pode selecionar o critério por `id` ou pelo texto exato em `criterion`:

| Campo | Semântica |
| --- | --- |
| `id` / `criterion` | Identifica exatamente um item de `acceptance` |
| `type: verifier` | Exige um único `VerificationResult` com o nome de `reference` e status `Pass` |
| `type: test` | Executa `dotnet test --filter <reference>` e exige ao menos um resultado TRX, todos aprovados |
| `test_path` | Arquivo de teste que deve aparecer como adicionado ou modificado para critério comportamental |
| `behavioral` | Quando `true`, exige `test_path` alterado; um verificador determinístico explícito pode ser usado como evidência equivalente |
| `equivalent_behavioral_evidence` | Autoriza um verificador não estrutural como prova comportamental equivalente; `false` por padrão |
| `required` | `true` por padrão; critérios opcionais sem prova não bloqueiam a decisão |

Critério obrigatório sem associação, associação para verificador ausente, teste filtrado que executa zero casos e resultado `Skip`, `Error` ou `Fail` são rejeitados de forma fechada. O exit code zero do processo de testes, isoladamente, não é aceito como prova do critério. A matriz persistida referencia o ID exato do `VerificationResult` ou do `ExecutionCommandEvidence` usado como prova.

### Escopo

| Campo | Tipo | Padrão | Semântica |
| --- | --- | --- | --- |
| `scope.allowed` | lista de caminhos | vazia | Se preenchida, todo arquivo relatado deve corresponder a ao menos um padrão |
| `scope.forbidden` | lista de caminhos | vazia | Padrões proibidos, avaliados antes dos permitidos |

Padrões suportados pelo verificador atual:

- caminho exato: `src/Customers/Customer.cs`;
- prefixo recursivo terminado em `/**`: `src/Customers/**`;
- `*` dentro de um segmento: `src/*.cs`;
- separadores `/` e `\` são normalizados.

Use caminhos relativos à raiz informada em `--repo`. Se um arquivo corresponder simultaneamente a `allowed` e `forbidden`, a proibição prevalece.

Os blocos são aplicados antes do gate de escopo, mas apenas no worktree descartável do candidato. Os caminhos passam antes por uma barreira preventiva contra destino absoluto, traversal, `.git`, symlink/junction e duplicidade. Depois, o verificador usa a lista derivada pelo Git — não a lista alegada pelo agente — e uma violação de escopo bloqueia a decisão. Nenhuma dessas mudanças chega automaticamente ao checkout original.

### Restrições e risco

| Campo | Valores reconhecidos | Padrão |
| --- | --- | --- |
| `constraints.security_risk` | `low`, `r0`, `r1`, `medium`, `r2`, `high`, `r3` | `low` |
| `constraints.database_migration` | boolean | `false` |
| `constraints.external_dependency` | boolean | `false` |

O risco declarado é apenas uma entrada. `RiskClassifier` reclassifica o contrato e pode elevá-lo conforme o texto ou o escopo:

- R0: documentação, comentários, formatação ou rename;
- R1: mudança simples que não aciona regras superiores;
- R2: regra de negócio, service, repository, controller, API, validação, contrato ou mais de três padrões permitidos;
- R3: autenticação, autorização, pagamentos, segredos, criptografia, migração declarada ou dependência externa;
- R4: infraestrutura, Docker, Kubernetes, deploy, CI/CD, produção, rede ou schema/migration no objetivo.

Embora seja aceito como texto, `security_risk: r0` é convertido inicialmente em R1; R0 é inferido a partir do objetivo. O parser também ainda não converte `security_risk: r4` diretamente em R4. Para tarefas críticas, descreva claramente a natureza de infraestrutura no objetivo e mantenha `approval.production: human`.

### Orçamento

| Campo | Tipo | Padrão |
| --- | --- | --- |
| `budget.tokens` | inteiro | `60000` |
| `budget.usd` | decimal | `0.20` |
| `budget.retries` | inteiro | `1` |
| `budget.wall_clock_seconds` | inteiro | `120` |
| `budget.max_files_changed` | inteiro | `10` |

`retries` é o número de novas tentativas permitido depois da chamada inicial. Portanto, `retries: 0` permite uma tentativa e `retries: 2` permite no máximo três. Somente falhas transitórias, HTTP 429 e timeout podem ser repetidos. Cancelamento, erro permanente, violação de política ou orçamento encerram a execução imediatamente.

O backoff exponencial começa em um segundo e é limitado a 30 segundos. Quando o provedor envia `Retry-After`, o maior valor entre o backoff e a espera solicitada é usado, ainda sujeito ao teto. Uma nova tentativa não começa quando a espera não cabe no wall clock restante.

O wall clock começa no início do pipeline e é compartilhado por preflight, agente, build, testes e testes de aceite filtrados. Cada subprocesso recebe somente o tempo global restante; timeout ou cancelamento encerra toda a árvore de processos. As etapas de limpeza do worktree e persistência de evidência continuam fora do token cancelado para preservar isolamento e auditabilidade.

Tokens e custo são acumulados entre todas as tentativas. Antes de cada chamada o adaptador recebe apenas o saldo restante; os adaptadores HTTP limitam a geração ao saldo de tokens/custo estimado. Caso a telemetria final ainda informe consumo excedente, a execução falha fechada e nenhuma nova tentativa é iniciada. O custo do Ollama é registrado como zero; o adaptador cloud usa uma estimativa baseada em tokens e numa tabela interna, não uma fatura do provedor.

Valores negativos para tokens, USD, retries ou arquivos e `wall_clock_seconds` menor ou igual a zero são rejeitados pelo parser.

### Perfil de execução

| Campo | Tipo | Padrão | Semântica |
| --- | --- | --- | --- |
| `execution.runtime` | `docker` ou `host` | `docker` | Runtime dos comandos que executam código/ferramentas do repositório staged |
| `execution.working_directory` | caminho relativo | `.` | Diretório, dentro do worktree isolado, onde build e testes são executados |
| `execution.target` | caminho relativo | vazio | Arquivo `.sln`, `.slnx`, `.csproj`, `.fsproj` ou `.vbproj` usado pelos comandos `dotnet build` e `dotnet test` |
| `execution.sandbox.image` | referência OCI | SDK .NET 9 fixado | Imagem imutável; tags sem `@sha256:<digest>` são rejeitadas |
| `execution.sandbox.cpu_limit` | decimal positivo | `1.0` | Limite de CPU passado ao Docker |
| `execution.sandbox.memory_limit` | limite Docker | `512m` | Limite de memória do container |
| `execution.sandbox.process_limit` | inteiro positivo | `128` | Limite de processos/PIDs |
| `execution.sandbox.wall_clock_seconds` | inteiro positivo | `120` | Teto por comando, sempre limitado também pelo orçamento global restante |
| `execution.sandbox.network_access` | boolean | `false` | `false` usa `--network none`; acesso precisa ser declarado explicitamente |

Quando build ou testes são obrigatórios, `execution.target` também é obrigatório. O AECS falha fechado antes de chamar o agente se o perfil estiver ausente ou inválido, se o diretório/target não existir ou se algum caminho tentar atravessar a fronteira do worktree.

O preflight usa um worktree exclusivo para compilar e testar o baseline. Somente depois de um baseline verde o AECS cria um segundo worktree limpo para o agente. Os mesmos diretório e target são reutilizados no candidato. Build, testes, testes de aceite e qualquer scanner externo usam requisições estruturadas diretamente no container, sem `sh -c`. Somente o worktree staged é montado; o checkout original, o socket Docker e caches do host não são expostos.

O container usa root filesystem read-only, `/tmp` limitado, capabilities removidas, `no-new-privileges`, limites de CPU/memória/PIDs/wall clock e rede negada por padrão. A imagem precisa estar fixada por digest. Runtime e versão, imagem/digest, rede, limites e tipo de mount ficam registrados em cada comando da evidência autenticada. Containers são removidos em sucesso, falha, timeout e cancelamento; o worktree também é removido pelo pipeline.

`runtime: host` existe somente para desenvolvimento confiável. Na CLI ele exige também `--allow-host-execution`, e a evidência marca `developmentHostOverride: true`. Se Docker for obrigatório e o daemon estiver ausente, a execução falha fechada antes de executar código do repositório. Os verificadores estruturais EB embutidos continuam sendo lógica do controlador que apenas lê o worktree; eles não carregam nem executam código do repositório.

Detalhes operacionais e o modelo de ameaça estão em [Sandbox Docker staged](docker-staged-sandbox.md).

### Contexto compilado

Não existe um campo separado de contexto no YAML atual. Depois do preflight, o AECS compila contexto C# diretamente do worktree do candidato usando objetivo, critérios de aceite e os padrões de `scope.allowed`/`scope.forbidden`.

Os limites padrão são 12 mil tokens estimados, 48 mil caracteres totais e 16 mil caracteres por arquivo; um `budget.tokens` positivo menor reduz o teto. O manifesto persistido informa arquivos incluídos e omitidos, símbolos, hashes do conteúdo original e incluído, truncamento, tamanho e baseline. Arquivos fora de `allowed` nunca entram no pacote, e `allowed` vazio resulta em contexto de código vazio.

### Verificação

| Campo | Padrão | Efeito de `required` |
| --- | --- | --- |
| `verification.build` | `required` | Build participa da decisão |
| `verification.unit_tests` | `required` | Testes participam da decisão |
| `verification.integration_tests` | `optional` | Quando required, ativa o mesmo gate/comando `Tests` usado por unit tests |
| `verification.scope` | `required` | Campo aceito, mas `Scope` é sempre um gate estrutural obrigatório na trust boundary atual |
| `verification.security_scan` | `optional` | Quando required, exige `SecurityScan`; como não há implementação, a decisão falha fechada por resultado ausente |
| `verification.architecture` | `optional` | Quando required, exige `EB001-Architecture` em `Pass` |
| `verification.critical_semantic_failures` | `required` | Falhas semânticas de severidade crítica bloqueiam a decisão |
| `verification.required_semantic_verifiers` | lista vazia | Nomes de verificadores EB que devem produzir exatamente um resultado `Pass` |

Somente o texto `required`, sem diferenciar maiúsculas de minúsculas, ativa esses campos. Qualquer outro valor é tratado como opcional.

A verificação de orçamento permanece habilitada pelo padrão do modelo. Embora alguns exemplos tragam `verification.budget`, o parser atual ignora esse campo e mantém `Budget = true`.

`AgentSuccess`, `Application`, `NonEmptyChange`, `Scope` e `Budget` são sempre obrigatórios, mesmo que um campo opcional tente enfraquecê-los. Build e testes só executam depois desses pré-requisitos; uma falha estrutural deixa os comandos posteriores em `Skip`, que não é aceito como sucesso.

Os verificadores EB001–EB005 executam depois dos pré-requisitos e de um eventual build aprovado. Por padrão, uma falha EB com severidade crítica bloqueia a decisão. Outros verificadores semânticos só viram gates quando declarados em `required_semantic_verifiers` ou ativados por um campo específico, como `architecture`. Exceção de verificador, resultado ausente ou duplicado também falha fechado.

### Aprovação

| Campo | Valores | Padrão |
| --- | --- | --- |
| `approval.production` | `none` ou `human` | `none` |

Com `human`, uma execução que passou nas verificações obrigatórias termina como `HumanReviewRequired`. Ela só pode ser promovida depois por `aecs promote --human-approval <referência>`; uma confirmação comum ou de política não substitui essa aprovação humana.

## Decisões possíveis

| Decisão | Condição |
| --- | --- |
| `Verified` | agente, aplicação, diff Git e todos os gates obrigatórios passaram, sem aprovação humana |
| `Rejected` | baseline, agente, limite, escopo, evidência de aceite ou outro gate obrigatório não passou |
| `HumanReviewRequired` | verificações passaram e a política exige uma pessoa |

O estado `Verified` exige sucesso do agente, aplicação válida, diff real, escopo e orçamento válidos, gates configurados em `Pass` e evidência executável para cada critério de aceite obrigatório. Isso prova a matriz declarada pelo contrato; não prova requisitos que não tenham sido escritos no contrato.

## Recomendações para escrever contratos

- use um `id` curto, único e estável;
- escreva um objetivo específico e sem combinar mudanças independentes;
- formule critérios de aceite observáveis e testáveis;
- permita o menor conjunto possível de caminhos;
- declare explicitamente diretórios sensíveis em `forbidden`;
- reserve margem de orçamento, mas mantenha `max_files_changed` baixo;
- exija revisão humana para autenticação, infraestrutura, deploy, banco e produção;
- use `--mock` apenas para exercitar composição e políticas; o candidato textual sintético não comprova o requisito de negócio;
- execute modelos reais somente sobre uma working tree limpa.

Veja modelos de contrato em [`tasks/`](../tasks/) e contratos executados de forma reproduzível no [fixture AgronomoPlus](../tests/fixtures/real-world-demo/README.md).
