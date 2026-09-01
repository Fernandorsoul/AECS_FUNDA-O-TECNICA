# Runtime compartilhado e configuração

`run`, experimentos, replay e o Jarvis usam o mesmo `AecsExecutionRuntime`. Esse
composition root cria a cadeia de adapters, o store de evidências, o runner staged e o sandbox.
O REPL não instancia Ollama, fallback ou pipeline por conta própria.

## Contrato versionado

Comandos de evidência, histórico e promoção também resolvem a seção `evidence` desse contrato,
sem construir um agent. O arquivo JSON usa o schema estrito `aecs.runtime-config/v1`. Copie
[`aecs.runtime.example.json`](../aecs.runtime.example.json) e informe o caminho com
`--runtime-config` ou `AECS_RUNTIME_CONFIG`:

```powershell
Copy-Item aecs.runtime.example.json aecs.runtime.local.json
dotnet run --project src/AECS.Cli/AECS.Cli.csproj -- jarvis `
  --repo . `
  --runtime-config aecs.runtime.local.json
```

Campos desconhecidos, duplicados, schema incompatível, endpoint inseguro e limites inválidos
falham antes da execução. A configuração não aceita API key nem connection string. Ela guarda
somente o nome da variável que contém a credencial; segredos continuam no ambiente, `.env` não
versionado ou secret manager.

## Precedência e proveniência

A resolução é determinística, do menor para o maior nível de precedência:

1. padrões seguros;
2. arquivo `aecs.runtime-config/v1`;
3. variáveis de ambiente;
4. flags da linha de comando.

O loader de `.env` preenche apenas variáveis ainda ausentes, portanto não sobrescreve o ambiente
do processo. `run`, `experiment`, `replay` e `jarvis` imprimem o schema, hash e origem efetiva de agent,
store, sandbox, profiles e policies. `--show-effective-config` apresenta o mesmo objeto em JSON.
Credenciais aparecem apenas como `configured` ou `unavailable`, acompanhadas da origem, nunca do
valor.

Flags compartilhadas:

```text
--runtime-config <arquivo>
--mock
--allow-host-execution
--ollama-url <url>
--ollama-context-window <tokens>
--enable-cloud-fallback | --disable-cloud-fallback
--allow-cloud-context
--cloud-key <segredo> | --cloud-key-env <variável>
--cloud-model <modelo>
--cloud-url <url>
--cloud-risks <R0,R1,...>
--evidence-store <json|postgres>
--evidence-root <diretório>
--key-directory <diretório>
--show-effective-config
```

Use `--cloud-key-env` em automação. `--cloud-key` permanece para compatibilidade local, mas o
resumo efetivo redige seu valor.

## Fallback cloud fechado por política

Ter uma credencial no ambiente não ativa cloud. A configuração precisa habilitar o fallback e
autorizar explicitamente o envio do contexto do repositório. Além disso:

- endpoint remoto precisa usar HTTPS; HTTP é aceito somente para loopback;
- pelo menos um risco deve estar na allowlist efetiva;
- a credencial configurada precisa existir antes da execução;
- uma execução fora da allowlist termina com `FallbackPolicyBlocked` e não chama o cloud;
- o adapter recebe apenas o budget restante e bloqueia resultado que exceda tokens ou custo;
- cancelamento, falha permanente e violação de política nunca acionam fallback.

Ollama continua sendo o primário. Falhas transitórias, indisponibilidade local, timeout e 429
podem acionar o cloud somente quando todas as condições anteriores forem satisfeitas.

## Fronteiras que a configuração não substitui

Profiles de execução, capabilities, rede do sandbox, secrets, gates e budget máximo continuam
sob autoridade do `TaskContract`. `--allow-host-execution` apenas permite que um contrato que já
solicita `execution.runtime: host` use o runner de desenvolvimento; ele não troca o runtime do
contrato silenciosamente. O store PostgreSQL exige `AECS_POSTGRES_CONNECTION_STRING` e nunca faz
fallback para JSON.
