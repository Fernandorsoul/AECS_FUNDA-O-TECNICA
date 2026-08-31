# Store PostgreSQL de evidências

O backend PostgreSQL implementa `IExecutionEvidenceStore` sem alterar o envelope autenticado. A execução completa fica em JSONB canônico e assinado; cada exportação ou promoção ocupa uma linha append-only com sequência, assinatura anterior e selo próprio.

## Modelo e garantias

`execution_evidence` mantém o agregado imutável, o selo inicial, a cabeça atual da cadeia e projeções indexadas de tarefa, `AgentRun` e candidato. O JSONB inclui contrato, tentativas, orçamento, baseline, comandos, contexto, critérios, verificações, decisão e transições.

`execution_evidence_promotion_events` mantém os eventos posteriores com FK para a execução e índice único `(ExecutionEvidenceId, Sequence)`. Constraints também fixam o schema do envelope, sequência positiva e contador não negativo. O store valida as projeções relacionais e todas as assinaturas antes de devolver dados.

`SaveAsync` é idempotente para o mesmo ID e conteúdo. `AppendPromotionAsync` bloqueia a linha da execução com `SELECT ... FOR UPDATE`, valida a cadeia dentro da transação e atualiza evento e cabeça de forma atômica. Repetir o mesmo ID de evento e payload não duplica a auditoria; reutilizar o ID com outro conteúdo falha.

## Configuração local

A connection string é um segredo. Forneça-a somente por `AECS_POSTGRES_CONNECTION_STRING`, `.env` ignorado pelo Git ou secret manager; não a passe como argumento da CLI.

Para o PostgreSQL do `docker-compose.yml`:

```powershell
$env:AECS_POSTGRES_PASSWORD = '<segredo-local>'
docker compose up -d postgres

$env:AECS_POSTGRES_CONNECTION_STRING = `
  "Host=localhost;Port=5432;Database=aecs;Username=aecs;Password=$env:AECS_POSTGRES_PASSWORD"
$env:AECS_EVIDENCE_STORE = 'postgres'
```

O keyring continua fora do banco e do repositório. Configure-o, quando necessário, com `AECS_EVIDENCE_KEY_DIRECTORY` ou `--key-directory`.

## Seleção na CLI

Todos os fluxos que produzem ou consomem evidência aceitam a mesma seleção:

```powershell
dotnet run --project src/AECS.Cli/AECS.Cli.csproj -- run `
  --repo C:\repos\alvo `
  --task-file C:\tasks\task.yaml `
  --evidence-store postgres
```

Use `--evidence-store postgres` também em `experiment`, `jarvis`, `promote` e `export-patch`. Para o backend local, selecione `--evidence-store json`; `--evidence-root` só é válido nesse modo. `AECS_EVIDENCE_STORE` define o padrão da sessão e, na ausência dessa variável, a compatibilidade atual permanece `json`.

Selecionar PostgreSQL sem a variável de conexão, com configuração inválida ou com o servidor indisponível encerra a operação. O AECS nunca muda para JSON silenciosamente.

## Migrations

O store executa migrations pendentes antes da primeira operação de cada processo. Em ambientes controlados, aplique-as antecipadamente com a mesma variável secreta:

```powershell
dotnet tool install --global dotnet-ef --version 8.0.0
dotnet ef database update `
  --project src/AECS.Infrastructure/AECS.Infrastructure.csproj `
  --startup-project src/AECS.Infrastructure/AECS.Infrastructure.csproj `
  --context AecsDbContext
```

Conceda ao usuário da aplicação somente as permissões necessárias. Se a política de produção separar DDL e DML, execute a migration com uma identidade administrativa antes de iniciar a CLI e use uma identidade operacional restrita depois.

Não existe importação automática de JSON. Evidências JSON assinadas continuam verificáveis no backend `json`; uma futura importação precisa preservar provenance e cadeia em vez de declarar implicitamente um arquivo como registro PostgreSQL original.

## Backup e recuperação

Banco e keyring formam o conjunto recuperável. Faça backup dos dois e proteja a chave privada separadamente:

```powershell
$env:PGHOST = 'localhost'
$env:PGPORT = '5432'
$env:PGDATABASE = 'aecs'
$env:PGUSER = 'aecs'
$env:PGPASSWORD = $env:AECS_POSTGRES_PASSWORD
pg_dump --format=custom --file aecs-evidence.dump
```

Restaure em banco vazio com `pg_restore`, recoloque o keyring protegido e valide uma amostra de evidências por `promote` ou `export-patch` antes de liberar escritores. Retenha todas as chaves públicas históricas. Backup somente do banco preserva os bytes assinados, mas não garante que a instalação conseguirá confiar nas chaves; backup somente das chaves perde a auditoria.

PostgreSQL melhora durabilidade, concorrência e recuperação pontual, mas um administrador capaz de restaurar banco e keyring para um snapshot antigo ainda pode produzir rollback integral. Detecção independente desse cenário exige uma âncora monotônica externa.

## Testes reais

O CI provisiona PostgreSQL 16 e executa os testes `Category=PostgreSql`. Localmente, depois de configurar a connection string:

```powershell
dotnet test tests/AECS.IntegrationTests/AECS.IntegrationTests.csproj `
  --filter "Category=PostgreSql"
```

Os cenários cobrem o agregado completo, idempotência, adulteração direta e oito escritores concorrentes sem perda de eventos.
