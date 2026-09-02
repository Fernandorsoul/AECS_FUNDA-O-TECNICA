# Replay determinístico de evidências

`aecs replay` verifica se uma execução autenticada ainda pode ser reproduzida sem chamar o agente. O comando carrega a evidência pelo mesmo backend usado na execução, valida schema, assinatura e cadeia, e exige o mesmo caminho de repositório autenticado:

```powershell
dotnet run --project src/AECS.Cli/AECS.Cli.csproj -- replay `
  --repo C:\repos\alvo `
  --evidence <evidence-id> `
  --evidence-store json
```

Use `--evidence-store postgres` para evidências no PostgreSQL. `--evidence-root` continua exclusivo do backend JSON, e `--key-directory` deve apontar para o keyring que confia na assinatura original.

## Fluxo e isolamento

O replay:

1. valida a versão e o fingerprint do TaskContract autenticado, ou marca explicitamente o contrato histórico como `legacy-v0`;
2. registra HEAD, branch e status do checkout original e recusa um repositório sujo;
3. confirma que o commit-base autenticado ainda existe;
4. cria um worktree detached descartável exatamente nesse commit;
5. compara versões de Git e .NET e reconstrói o `RepositorySnapshot` da baseline;
6. reconstrói o `CSharpSymbolGraph` com os limites originais e compara seu hash ao autenticado;
7. compara os hashes esperado/observado do snapshot e calcula arquivos adicionados, removidos ou alterados;
8. repete o preflight de build/test;
9. aplica no worktree apenas o diff persistido e assinado, sem interpretar a antiga resposta do agente;
10. deriva novamente o `CandidateChangeSet` pelo Git e compara hash e arquivos;
11. repete build, o gate legado `Tests` ou a matriz versionada `UnitTests`/`IntegrationTests`/`AcceptanceTests`, verificadores EB determinísticos disponíveis e evidências de aceite;
12. compara definições e resultados dos comandos, gates e referências de artefatos;
13. remove o worktree e confirma que HEAD, branch e index originais não mudaram.

O agente não é uma dependência do serviço de replay e nunca é chamado. O diff autenticado é a única entrada capaz de reconstruir o candidato.

## Resultados

| Resultado | Significado |
| --- | --- |
| `Reproduced` | snapshot, grafo semântico, hash do candidato, ferramentas, comandos, gates e artefatos equivalentes coincidem |
| `BaselineUnavailable` | o commit-base não existe mais no repositório informado |
| `CandidateDivergence` | o patch não aplica ou o Git deriva outro hash/conjunto de arquivos |
| `EnvironmentDivergence` | o candidato é idêntico, mas snapshot, grafo semântico, versão, comando, gate ou artefato diverge |
| `GateNotReproducible` | a execução exigia uma ferramenta ou gate indisponível no runtime de replay |
| `Failed` | validação do repositório ou outra etapa operacional falhou de forma fechada |

Uma evidência original válida recebe um `replayEvent` assinado com o resultado, comparações e timestamps. Para snapshots e grafos novos, o evento contém os hashes esperado/observado do inventário e do grafo, além de um `RepositorySnapshotDiff` ordenado. O evento referencia a execução e o candidato originais e compartilha a mesma sequência global das promoções. Evidência adulterada é recusada antes da criação de qualquer novo evento confiável.

O replay demonstra reprodutibilidade sob o contrato e os gates registrados. Ele não transforma um contrato incompleto em prova de correção, nem elimina fontes externas não fixadas, feeds mutáveis ou dependências removidas.
