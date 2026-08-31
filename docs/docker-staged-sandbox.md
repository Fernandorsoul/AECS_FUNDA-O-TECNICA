# Sandbox Docker staged

O pipeline staged separa operações de controle de comandos potencialmente não confiáveis. Git cria, inspeciona e remove worktrees no host; comandos que carregam código ou ferramentas do repositório são enviados como `ProcessExecutionRequest` estruturada para um container exclusivo.

## Política padrão

- runtime `docker` quando `execution.runtime` é omitido;
- imagem OCI imutável, obrigatoriamente fixada por `@sha256:<digest>`;
- somente o worktree temporário `aecs-staging-*` montado como `/workspace:rw`;
- root filesystem read-only e `/tmp` tmpfs de 64 MiB;
- `--network none`, `--cap-drop ALL`, `no-new-privileges` e init mínimo;
- limites configuráveis de CPU, memória, PIDs e wall clock;
- nenhum shell intermediário: executável e argumentos são enviados separadamente ao Docker;
- nenhuma montagem do checkout original, socket Docker, credenciais ou caches do host.

A imagem padrão é o SDK .NET 9 multi-arch fixado pelo digest declarado em `SandboxExecutionProfile.DefaultImage`. Dependências que não estejam na imagem ou no próprio worktree não podem ser restauradas com a política sem rede. Um contrato pode declarar `network_access: true`, mas essa ampliação fica visível na evidência e deve ser tratada como exceção de risco.

## Falha fechada e limpeza

Antes do primeiro comando, o pipeline consulta o servidor Docker. Daemon ausente, imagem sem digest, limite inválido ou tentativa de montar um diretório que não seja um worktree AECS interrompe a execução antes de rodar código do repositório.

Cada `docker run` recebe nome único, `--rm` e uma remoção forçada adicional em `finally`. Assim, sucesso, exit code não zero, timeout e cancelamento seguem a mesma limpeza. O pipeline remove o worktree staged separadamente, também fora do token cancelado. Não são criados volumes Docker nomeados.

## Evidência e replay

Cada `ExecutionCommandEvidence` registra:

- runtime e versão do servidor Docker;
- nome da imagem e digest;
- modo de rede;
- limites de CPU, memória, PIDs e wall clock;
- mount lógico do workspace;
- se houve override de desenvolvimento no host.

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

A CI baixa previamente a imagem pelo digest e executa uma suíte Docker real. Ela comprova o pipeline build/test/aceite, ausência do checkout original no container, rede bloqueada, limites cgroup e ausência de containers residuais após sucesso, falha, timeout e cancelamento.
