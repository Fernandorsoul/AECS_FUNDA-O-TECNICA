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
      reference: UnitTests
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
    test_suites:
      version: aecs.test-suites/v1
      unit:
        mode: required
        target: tests/SampleProject.UnitTests/SampleProject.UnitTests.csproj
        arguments: ["--configuration", "Release"]
        timeout_seconds: 45
      integration:
        mode: optional
        target: tests/SampleProject.IntegrationTests/SampleProject.IntegrationTests.csproj
        timeout_seconds: 60
      acceptance:
        mode: required
        target: tests/SampleProject.UnitTests/SampleProject.UnitTests.csproj
        timeout_seconds: 45
    sandbox:
      image: mcr.microsoft.com/dotnet/sdk:9.0@sha256:f190d2dd9eef2899c91ac323caa0bd2b39334a5400ba93013e5199da39dad940
      cpu_limit: "1.0"
      memory_limit: 512m
      process_limit: 128
      wall_clock_seconds: 120
      network_access: false
  capabilities:
    version: aecs.capabilities/v1
    file_system:
      read:
        - "**"
      write:
        - "**/bin/**"
        - "**/obj/**"
        - .aecs-verification/**
    processes:
      - executable: git
        argument_prefix: ["--version"]
        phases: [baseline.tool-probe]
      - executable: dotnet
        argument_prefix: ["--version"]
        phases: [baseline.tool-probe]
      - executable: dotnet
        argument_prefix: [build]
        phases: [baseline.build, candidate.build]
      - executable: dotnet
        argument_prefix: [test]
        phases:
          - baseline.unit-test
          - baseline.integration-test
          - baseline.acceptance-test
          - candidate.unit-test
          - candidate.integration-test
          - candidate.acceptance-test
          - candidate.acceptance
      - executable: dotnet
        argument_prefix: [list]
        phases: [baseline.security-scan, candidate.security-scan]
    network:
      destinations: []
      phases: []
    secrets: []
    resources:
      cpu_limit: "1.0"
      memory_limit: 512m
      process_limit: 128
      wall_clock_seconds: 120
  verification:
    build: required
    scope: required
    security_scan: required
    security_policy:
      version: aecs.security-scan/v1
      scanners: [secrets, dependencies, patterns]
      block_at_or_above: error
      vulnerability_database_version: aecs.nuget-advisories/2026-08-31
      suppressions: []
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
| `execution.target` | caminho relativo | vazio | Arquivo `.sln`, `.slnx`, `.csproj`, `.fsproj` ou `.vbproj` usado por build, gate legado de testes e inventário de dependências |
| `execution.test_suites` | objeto versionado | ausente | Ativa os gates independentes `UnitTests`, `IntegrationTests` e `AcceptanceTests` |
| `execution.sandbox.image` | referência OCI | SDK .NET 9 fixado | Imagem imutável; tags sem `@sha256:<digest>` são rejeitadas |
| `execution.sandbox.cpu_limit` | decimal positivo | `1.0` | Limite de CPU passado ao Docker |
| `execution.sandbox.memory_limit` | limite Docker | `512m` | Limite de memória do container |
| `execution.sandbox.process_limit` | inteiro positivo | `128` | Limite de processos/PIDs |
| `execution.sandbox.wall_clock_seconds` | inteiro positivo | `120` | Teto por comando, sempre limitado também pelo orçamento global restante |
| `execution.sandbox.network_access` | boolean | `false` | `false` usa `--network none`; acesso precisa ser declarado explicitamente |

Quando build ou o gate legado de testes são obrigatórios, `execution.target` também é obrigatório. Em `test_suites/v1`, cada suite habilitada declara seu próprio target. O AECS falha fechado antes de chamar o agente se o perfil estiver ausente ou inválido, se o diretório/target não existir ou se algum caminho tentar atravessar a fronteira do worktree.

#### Matriz de suites `aecs.test-suites/v1`

`execution.test_suites.version: aecs.test-suites/v1` substitui o agregado ambíguo `Tests` por três comandos e resultados identificáveis. O bloco contém `unit`, `integration` e `acceptance`; cada categoria aceita:

| Campo | Regra |
| --- | --- |
| `mode` | `required` executa e bloqueia; `optional` executa e informa sem bloquear; `disabled` não executa |
| `target` | Solução ou projeto relativo ao `working_directory`; obrigatório quando o gate está habilitado |
| `arguments` | Argumentos adicionais estruturados de `dotnet test`; logger, results directory e `--no-build` pertencem ao controlador |
| `timeout_seconds` | Teto positivo do comando, ainda limitado pelo wall clock global restante e pelo sandbox |

Baseline e candidato executam exatamente as mesmas categorias habilitadas, em ordem `unit`, `integration`, `acceptance`. Cada comando recebe `--no-build`, logger TRX e results directory isolado. A evidência `aecs.test-suite-evidence/v1` registra categoria, modo, target, argv, vínculo ao comando e quantidades descobertas, executadas, aprovadas, falhas e ignoradas. Um gate obrigatório sem TRX ou com zero testes executados produz `Fail`; resultado obrigatório ausente continua sendo rejeitado pelo `DecisionEngine`.

Testes de aceite filtrados por `acceptance_evidence.type: test` usam o target/argumentos da categoria `acceptance`, exigem um `AcceptanceTests` em `Pass` e continuam produzindo prova específica do candidato. Por isso, contratos v1 com esse tipo de evidência não podem desabilitar a categoria `acceptance`.

Compatibilidade é explícita: contratos sem `execution.test_suites` preservam `verification.unit_tests`/`integration_tests` e o único gate legado `Tests`, inclusive no replay de evidências autenticadas antigas. Novos contratos devem usar v1; não misture os campos legados para definir a política das suites, pois os modos v1 são autoritativos.

O preflight usa um worktree exclusivo para compilar e testar o baseline. Somente depois de todos os gates obrigatórios da baseline passarem o AECS cria um segundo worktree limpo para o agente; falhas opcionais permanecem visíveis sem bloquear. A mesma matriz é reutilizada no candidato. Build, suites, testes de aceite filtrados e qualquer scanner externo usam requisições estruturadas diretamente no container, sem `sh -c`. Somente o worktree staged é montado; o checkout original, o socket Docker e caches do host não são expostos.

O container usa root filesystem read-only, `/tmp` limitado, capabilities removidas, `no-new-privileges`, limites de CPU/memória/PIDs/wall clock e rede negada por padrão. A imagem precisa estar fixada por digest. Runtime e versão, imagem/digest, rede, limites e tipo de mount ficam registrados em cada comando da evidência autenticada. Containers são removidos em sucesso, falha, timeout e cancelamento; o worktree também é removido pelo pipeline.

`runtime: host` existe somente para desenvolvimento confiável. Na CLI ele exige também `--allow-host-execution`, e a evidência marca `developmentHostOverride: true`. Se Docker for obrigatório e o daemon estiver ausente, a execução falha fechada antes de executar código do repositório. Os verificadores estruturais EB embutidos continuam sendo lógica do controlador que apenas lê o worktree; eles não carregam nem executam código do repositório.

Detalhes operacionais e o modelo de ameaça estão em [Sandbox Docker staged](docker-staged-sandbox.md).

### Capabilities preventivas

`capabilities.version: aecs.capabilities/v1` é a autoridade preventiva dos comandos staged. O bloco pode ficar diretamente sob `task` ou dentro de `task.execution`; em novos contratos, prefira a primeira forma. Quando ele é omitido, o parser materializa uma política restritiva: leitura somente do worktree, escrita apenas em `bin`, `obj` e `.aecs-verification`, comandos `git --version`, `dotnet --version`, `dotnet build`, `dotnet test` e `dotnet list`, rede e segredos vazios e recursos limitados a `1.0` CPU, `512m`, 128 PIDs e 120 segundos. O default legado preserva apenas as fases agregadas antigas; contratos `test_suites/v1` recebem as fases específicas das categorias habilitadas e mantêm `candidate.acceptance` para a prova filtrada, sem alterar hashes de políticas históricas.

Se o bloco for declarado, campos ausentes não herdam permissões de filesystem, processos, rede ou segredos. Uma versão desconhecida, regra incompleta, fase desconhecida, caminho inseguro ou ausência de um comando exigido pelos gates faz o parse/preflight falhar antes do agente. A autoridade é definida pelo contrato (`task-contract`), não pode ser substituída no YAML, e qualquer plano adaptativo é comparado com ela para impedir expansão.

| Campo | Regra em `aecs.capabilities/v1` |
| --- | --- |
| `file_system.read` | Deve ser exatamente `["**"]`; o mount expõe somente o worktree staged, nunca o checkout original |
| `file_system.write` | Aceita `**/bin/**`, `**/obj/**`, um diretório relativo terminado em `/**` ou `**` como ampliação explícita |
| `processes[].executable` | Nome simples do executável, sem caminho ou shell |
| `processes[].argument_prefix` | Prefixo não vazio de argv; o comando só é aceito quando executável, prefixo e fase coincidem |
| `processes[].phases` | Uma ou mais fases conhecidas da tabela abaixo |
| `network.destinations` / `network.phases` | As duas listas ficam vazias para negar rede ou são declaradas juntas; `network_access` também precisa estar ativo |
| `secrets[].name` | Nome de variável de ambiente em maiúsculas; o valor vem do ambiente do controlador somente nas fases declaradas |
| `resources` | Tetos de CPU, memória, PIDs e wall clock; os limites de `execution.sandbox` não podem excedê-los |

Fases reconhecidas: `baseline.tool-probe`, `baseline.build`, `baseline.test`, `baseline.unit-test`, `baseline.integration-test`, `baseline.acceptance-test`, `baseline.security-scan`, `candidate.build`, `candidate.test`, `candidate.unit-test`, `candidate.integration-test`, `candidate.acceptance-test`, `candidate.security-scan` e `candidate.acceptance`. Build, cada suite habilitada, scan de dependências e aceite filtrado exigem antecipadamente as regras de processo correspondentes. O processo recebe argv estruturado, portanto o prefixo não é reinterpretado por um shell.

No Docker, `/workspace` é read-only e cada diretório autorizado recebe um bind mount gravável separado. Caminhos absolutos, traversal, `.git`, wildcards não suportados, symlinks, junctions/reparse points e escapes por mount são recusados. A implementação atual consegue aplicar egress somente como `none` ou Docker `bridge`: uma lista contendo explicitamente `"*"` autoriza `bridge` nas fases indicadas; destinos mais estreitos falham fechados até existir um enforcer de egress por destino.

Segredos nunca são incluídos no prompt. O Docker recebe apenas `--env NOME`, sem o valor no argv; o nome e a concessão entram na evidência, mas stdout e stderr de qualquer fase que receba segredo são integralmente suprimidos para impedir vazamento direto ou codificado. Use uma fase dedicada e o menor conjunto de nomes possível.

Cada comando registra versão, autoridade e hash da política, fase, concessões, recusas e nomes dos segredos injetados. O replay exige a mesma política para evidências novas; registros autenticados anteriores ao schema v1 usam uma política de compatibilidade marcada como legado, sem alterar o payload assinado original.

### Contexto compilado

Não existe um campo separado de contexto no YAML atual. Depois do preflight, o AECS compila contexto C# diretamente do worktree do candidato usando objetivo, critérios de aceite e os padrões de `scope.allowed`/`scope.forbidden`.

Os limites padrão são 12 mil tokens estimados, 48 mil caracteres totais e 16 mil caracteres por arquivo; um `budget.tokens` positivo menor reduz o teto. O manifesto persistido informa arquivos incluídos e omitidos, símbolos, hashes do conteúdo original e incluído, truncamento, tamanho e baseline. Arquivos fora de `allowed` nunca entram no pacote, e `allowed` vazio resulta em contexto de código vazio.

### Verificação

| Campo | Padrão | Efeito de `required` |
| --- | --- | --- |
| `verification.build` | `required` | Build participa da decisão |
| `verification.unit_tests` | `required` | Compatibilidade legada: ativa o agregado `Tests` quando `execution.test_suites` está ausente |
| `verification.integration_tests` | `optional` | Compatibilidade legada: quando required, ativa o mesmo agregado `Tests` |
| `verification.scope` | `required` | Campo aceito, mas `Scope` é sempre um gate estrutural obrigatório na trust boundary atual |
| `verification.security_scan` | `optional` | Quando required, inventaria a baseline e bloqueia achados novos do candidato conforme `security_policy` |
| `verification.architecture` | `optional` | Quando required, exige `EB001-Architecture` em `Pass` |
| `verification.critical_semantic_failures` | `required` | Falhas semânticas de severidade crítica bloqueiam a decisão |
| `verification.required_semantic_verifiers` | lista vazia | Nomes de verificadores EB que devem produzir exatamente um resultado `Pass` |

Somente o texto `required`, sem diferenciar maiúsculas de minúsculas, ativa esses campos. Qualquer outro valor é tratado como opcional.

A verificação de orçamento permanece habilitada pelo padrão do modelo. Embora alguns exemplos tragam `verification.budget`, o parser atual ignora esse campo e mantém `Budget = true`.

`AgentSuccess`, `Application`, `NonEmptyChange`, `Scope` e `Budget` são sempre obrigatórios, mesmo que um campo opcional tente enfraquecê-los. Build e testes só executam depois desses pré-requisitos; uma falha estrutural deixa os comandos posteriores em `Skip`, que não é aceito como sucesso.

Os verificadores EB001–EB005 executam depois dos pré-requisitos e de um eventual build aprovado. Por padrão, uma falha EB com severidade crítica bloqueia a decisão. Outros verificadores semânticos só viram gates quando declarados em `required_semantic_verifiers` ou ativados por um campo específico, como `architecture`. Exceção de verificador, resultado ausente ou duplicado também falha fechado.

#### Gate SecurityScan

`security_scan: required` ativa uma política versionada `aecs.security-scan/v1`. Sem `security_policy`, os defaults executam `secrets`, `dependencies` e `patterns`, bloqueiam severidade `error` ou `critical` e usam a base fixa `aecs.nuget-advisories/2026-08-31`.

| Campo de `verification.security_policy` | Regra |
| --- | --- |
| `version` | Deve ser `aecs.security-scan/v1` |
| `scanners` | IDs únicos entre `secrets`, `dependencies` e `patterns`; ao menos um é obrigatório |
| `block_at_or_above` | `info`, `warning`, `error`/`high` ou `critical` |
| `vulnerability_database_version` | Deve selecionar a snapshot suportada, nunca um alias móvel como `latest` |
| `suppressions` | Cada item exige `rule`, `path` relativo seguro, justificativa de ao menos 10 caracteres e, opcionalmente, fingerprint SHA-256 exato |

Os scanners de segredos e padrões leem apenas extensões textuais conhecidas no worktree staged, sem seguir links e sem carregar código. O inventário de dependências executa `dotnet list <target> package --include-transitive --format json` por argv estruturado no mesmo runner Docker dos demais gates, nas fases `baseline.security-scan` e `candidate.security-scan`. No SDK .NET 9 esse comando lê os assets produzidos pelo build anterior; o sandbox permanece sem rede. O parser tolera o aviso textual de workload que o SDK pode antepor, mas exige que o restante da saída seja um documento JSON completo. `execution.target` e os artefatos de restore/build precisam existir; ferramenta ausente, exit code não zero, timeout, cancelamento ou JSON inválido tornam o gate inconclusivo e resultam em `Error` crítico.

A base embutida é deliberadamente pequena e reproduzível. A versão atual cobre [GHSA-5crp-9r3c-p9vr](https://github.com/advisories/GHSA-5crp-9r3c-p9vr) para `Newtonsoft.Json < 13.0.1` e [GHSA-ghhp-997w-qr28](https://github.com/advisories/GHSA-ghhp-997w-qr28) para as faixas afetadas de `System.Text.Encodings.Web`. Alterar regras ou advisories exige uma nova versão da snapshot e testes correspondentes.

A baseline é sempre inventariada primeiro. Achados conclusivos nela recebem disposição `Baseline` e não bloqueiam por si só; no candidato, o mesmo fingerprint continua como dívida existente, enquanto fingerprints inéditos recebem `New`. Supressões exatas e justificadas recebem `Suppressed`. Somente achados `New`, não suprimidos e iguais ou superiores ao limiar bloqueiam.

Cada finding persiste scanner, categoria, arquivo, linha, regra, severidade, advisory, disposição e fingerprint — nunca o trecho correspondente. Saída de ferramenta tem caminhos do staging normalizados, credenciais em URL/tokens sanitizadas e limite de tamanho antes de entrar em `ExecutionCommandEvidence`. A evidência registra ainda schema, hash da política, versões dos scanners/configurações e da base, resultado conclusivo e contagens. O replay repete o scan e exige igualdade dessa evidência, não apenas do status do gate. Implementações podem substituir scanners por ID através de `ISecurityScanner`, sem alterar a política autoritativa.

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
