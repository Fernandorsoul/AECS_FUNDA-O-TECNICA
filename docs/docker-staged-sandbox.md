# Sandbox Docker staged

O pipeline staged separa operações de controle de comandos potencialmente não confiáveis. Git cria, inspeciona e remove worktrees no host; comandos que carregam código ou ferramentas do repositório são enviados como `ProcessExecutionRequest` estruturada para um container exclusivo.

## Política padrão

- runtime `docker` quando `execution.runtime` é omitido;
- imagem OCI imutável, obrigatoriamente fixada por `@sha256:<digest>`;
- somente o worktree temporário `aecs-staging-*` montado como `/workspace:ro`, com submounts graváveis explícitos para os caminhos autorizados;
- root filesystem read-only e `/tmp` tmpfs de 64 MiB;
- `--network none`, `--cap-drop ALL`, `no-new-privileges` e init mínimo;
- limites configuráveis de CPU, memória, PIDs e wall clock;
- nenhum shell intermediário: executável e argumentos são enviados separadamente ao Docker;
- nenhuma montagem do checkout original, socket Docker, credenciais ou caches do host.

A imagem padrão é o SDK .NET 10 multi-arch fixado pelo digest declarado em `SandboxExecutionProfile.DefaultImage`. Ela acompanha o TFM `net10.0` dos binários AECS, conforme a [matriz de suporte .NET](dotnet-support.md). Dependências e runtimes que não estejam na imagem ou no próprio worktree não podem ser restaurados com a política sem rede.

Além do perfil de sandbox, cada contrato possui uma política `aecs.capabilities/v1`, com autoridade `task-contract`. Antes de chamar o Docker, o runner confronta executável, prefixo de argv e fase; calcula os mounts graváveis; valida recursos, rede e segredos; e falha fechado se não houver uma concessão correspondente. A política padrão permite somente os probes, build, testes e diretórios de saída necessários ao pipeline.

`network_access: true` é apenas o primeiro de dois controles: a política também precisa autorizar a fase e os destinos. O runtime atual não oferece filtragem segura por hostname; por isso, apenas o destino explícito `"*"` habilita Docker `bridge`. Destinos mais estreitos são recusados até que exista um proxy/enforcer de egress. Sem as duas autorizações, permanece `--network none`.

Segredos declarados são buscados no ambiente do controlador somente para a fase autorizada. O comando Docker recebe o nome da variável, nunca o valor em argv; a política registra somente o nome e suprime integralmente stdout/stderr da fase para bloquear exfiltração direta ou codificada. Nenhum segredo é enviado ao prompt do agente.

## Falha fechada e limpeza

Antes do primeiro comando, o pipeline consulta o servidor Docker. Daemon ausente, imagem sem digest, limite inválido, capability ausente ou tentativa de montar um diretório que não seja um worktree AECS interrompe a execução antes de rodar código do repositório. Os caminhos graváveis são resolvidos novamente antes de cada comando; traversal, `.git`, symlink, junction/reparse point e escape para fora do worktree são recusados antes do `docker run`.

Cada `docker run` recebe nome único, `--rm` e uma remoção forçada adicional em `finally`. Assim, sucesso, exit code não zero, timeout e cancelamento seguem a mesma limpeza. O pipeline remove o worktree staged separadamente, também fora do token cancelado. Não são criados volumes Docker nomeados.

## Evidência e replay

Cada `ExecutionCommandEvidence` registra:

- runtime e versão do servidor Docker;
- nome da imagem e digest;
- modo de rede;
- limites de CPU, memória, PIDs e wall clock;
- mount lógico do workspace;
- se houve override de desenvolvimento no host;
- versão, autoridade e hash da política de capabilities;
- fase, concessões, recusas e nomes — nunca valores — de segredos injetados.

O replay autenticado recria o mesmo runtime a partir do contrato assinado e compara esses metadados junto com a definição e o resultado dos comandos. Evidências anteriores ao campo de ambiente continuam verificáveis; nelas, a ausência do campo é tratada como formato legado.

## Desenvolvimento no host

Para depuração local confiável, declare:

```yaml
execution:
  runtime: host
  target: MinhaSolucao.slnx
```

E execute a CLI com `--allow-host-execution`. As duas escolhas são necessárias. Os comandos ficam marcados com `developmentHostOverride: true`, rede `host` e limites `unlimited`; portanto, esse modo não deve ser usado para contratos ou repositórios não confiáveis.

## Verificação em CI

A CI baixa previamente a imagem pelo digest e executa uma suíte Docker real. Ela comprova o pipeline build/test/`SecurityScan`/aceite, root do workspace read-only, escrita no submount autorizado, inventário estruturado de dependências, bloqueio preventivo de processo, rede e symlink de exfiltração, limites cgroup e ausência de containers residuais após sucesso, falha, timeout e cancelamento.
