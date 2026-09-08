# AECS 0.1.0-pilot

Source SHA: `preencher`

Package: `aecs-cli-0.1.0-pilot.zip`

Package SHA-256: `sha256:preencher`

CI run: `preencher`

Independent acceptance reviewer: `preencher`

## Promessa da versão

O AECS executa tarefas de programação C#/.NET com limites explícitos, verifica resultados e permite
revisar/promover mudanças com rastreabilidade. Economia comprovada, suporte geral a linguagens e
melhoria adaptativa não fazem parte da promessa inicial.

## Funcionalidades incluídas

- TaskContract `aecs.task-contract/v1` com schema estrito e fingerprint canônico.
- Execução staged com sandbox Docker por padrão e evidência autenticada.
- Diagnóstico `aecs doctor`.
- Jarvis com `guide`, `run`, `status`, `history`, `explain`, `context`, `review` e `export-patch`.
- Promoção controlada com confirmação literal pelo hash.
- Replay, Evidence Graph, store JSON/PostgreSQL e cliente VS Code mínimo.
- Demonstração reproduzível com cenários de aceite, rejeição e violação de escopo.

## Matriz de suporte

- SDK/build: .NET 10 LTS estável.
- Runtime do produto: `net10.0`.
- Sandbox staged: imagem .NET 10 fixada por digest.
- VS Code: 1.95+ para o cliente mínimo.
- Sistemas validados: preencher a partir do pacote final.

## Limitações e incompatibilidades

- Docker é obrigatório para o staged runner padrão.
- Provider real local/cloud exige configuração e autorização explícitas.
- O cliente VS Code não promove patches.
- O roteamento adaptativo permanece em shadow/offline; #35/#72/#89 continuam fora da promessa.
- O keyring local não substitui KMS/HSM nem transparência imutável.

## Atualização e rollback

1. Faça backup do diretório de evidências e do keyring juntos.
2. Instale o pacote novo em diretório separado.
3. Rode `aecs doctor --repo <repo> --format json`.
4. Faça uma execução mock curta e uma inspeção de evidência.
5. Para rollback, volte o binário anterior e preserve evidências/keyring sem migração destrutiva.

## Feedback e suporte

Abra issues usando o template de bug do piloto e inclua somente dados redigidos: versão, SHA,
`doctor --format json`, evidence IDs/hashes, logs sem segredo, sistema operacional e reprodução
mínima.
