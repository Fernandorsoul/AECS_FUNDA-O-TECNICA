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

1. registra HEAD, branch e status do checkout original e recusa um repositório sujo;
2. confirma que o commit-base autenticado ainda existe;
3. cria um worktree detached descartável exatamente nesse commit;
4. compara versões de Git e .NET e repete o preflight de build/test;
5. aplica no worktree apenas o diff persistido e assinado, sem interpretar a antiga resposta do agente;
6. deriva novamente o `CandidateChangeSet` pelo Git e compara hash e arquivos;
7. repete build, o gate legado `Tests` ou a matriz versionada `UnitTests`/`IntegrationTests`/`AcceptanceTests`, verificadores EB determinísticos disponíveis e evidências de aceite;
8. compara definições e resultados dos comandos, gates e referências de artefatos;
9. remove o worktree e confirma que HEAD, branch e index originais não mudaram.

O agente não é uma dependência do serviço de replay e nunca é chamado. O diff autenticado é a única entrada capaz de reconstruir o candidato.

## Resultados

| Resultado | Significado |
| --- | --- |
| `Reproduced` | hash do candidato, ferramentas, comandos, gates e artefatos equivalentes coincidem |
| `BaselineUnavailable` | o commit-base não existe mais no repositório informado |
| `CandidateDivergence` | o patch não aplica ou o Git deriva outro hash/conjunto de arquivos |
| `EnvironmentDivergence` | o candidato é idêntico, mas versão, comando, gate ou artefato diverge |
| `GateNotReproducible` | a execução exigia uma ferramenta ou gate indisponível no runtime de replay |
| `Failed` | validação do repositório ou outra etapa operacional falhou de forma fechada |

Uma evidência original válida recebe um `replayEvent` assinado com o resultado, comparações e timestamps. O evento referencia a execução e o candidato originais e compartilha a mesma sequência global das promoções. Evidência adulterada é recusada antes da criação de qualquer novo evento confiável.

O replay demonstra reprodutibilidade sob o contrato e os gates registrados. Ele não transforma um contrato incompleto em prova de correção, nem elimina fontes externas não fixadas, feeds mutáveis ou dependências removidas.
