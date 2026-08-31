# RepositorySnapshot determinístico

O `RepositorySnapshot` descreve a estrutura versionada da baseline antes de qualquer build, teste ou chamada ao agente. Ele transforma a árvore Git autenticada em um inventário ordenado e endereçado por conteúdo, suficiente para auditoria, replay e detecção de mudança sem armazenar código-fonte ou segredos.

## Origem e estratégia

O builder opera dentro do worktree detached de preflight, mas usa `git ls-tree -r -l -z --full-tree <baseline>` como autoridade. Por isso, arquivos untracked, artefatos gerados pelo build e diferenças de line ending do checkout não alteram o resultado. Cada arquivo incluído recebe caminho relativo, tamanho, tipo, linguagem e o object ID do blob no formato `git:<oid>`.

As versões persistidas são:

| Campo | Valor atual | Papel |
| --- | --- | --- |
| `schemaVersion` | `aecs.repository-snapshot/v1` | contrato do documento persistido |
| `strategyVersion` | `git-tree-manifest-inventory/v1` | regras de descoberta e classificação |
| `profileVersion` | `aecs.repository-snapshot-profile/v1` | configuração aceita no TaskContract |

Uma versão futura precisa de novo identificador e compatibilidade explícita; não pode mudar silenciosamente o significado dos hashes v1.

## Inventário

O snapshot contém:

- arquivos e contagem agregada de linguagens;
- soluções `.sln`/`.slnx`, projetos `.csproj`/`.fsproj`/`.vbproj` e suas relações;
- SDK, output type, frameworks, referências de projetos e pacotes .NET;
- manifests de .NET, npm, Python, Go, Cargo, JVM e container;
- dependências NuGet/npm conhecidas, com especificadores npm não versionados sanitizados;
- entrypoints por convenção e projetos executáveis;
- suites declaradas no TaskContract, gate legado e projetos de teste descobertos;
- proveniência das ferramentas sondadas na baseline: disponibilidade, versão, runtime e digest da imagem.

XML é analisado com DTD proibido e sem resolver entidades. Manifests reconhecidos têm limite de 8 MiB e precisam resolver para arquivo regular dentro do worktree, sem traversal ou reparse point. Documento inválido ou inseguro falha fechado.

## Hashes e determinismo

`configurationHash` é SHA-256 do perfil versionado e da lista efetiva/ordenada de exclusões. `snapshotHash` é SHA-256 do schema, estratégia, configuração e inventário derivado, com listas ordenadas ordinalmente.

O endereço de conteúdo não inclui `baselineCommit`, `taskContractId`, ferramentas nem `excludedEntryCount`. Esses campos continuam no snapshot e são cobertos pela assinatura da evidência, mas representam proveniência em vez do conteúdo inventariado. Assim:

- a mesma árvore incluída e a mesma configuração produzem o mesmo hash em hosts diferentes;
- alterar somente um diretório excluído não altera o hash;
- alterar, adicionar ou remover um arquivo incluído altera o hash;
- trocar o perfil/estratégia altera o hash mesmo se os arquivos forem iguais.

Na leitura, o store autenticado recalcula os dois hashes e valida vínculos, caminhos, IDs Git, duplicidade e relações antes de expor a evidência.

## Exclusões e dados sensíveis

São excluídos por padrão diretórios de VCS/IDE, caches, dependências materializadas, resultados de testes, builds e distribuições. O TaskContract pode acrescentar diretórios relativos:

```yaml
execution:
  repository_snapshot:
    version: aecs.repository-snapshot-profile/v1
    excluded_directories:
      - vendor/generated
```

A correspondência dos defaults vale para qualquer segmento com o nome conhecido; exclusões configuradas são relativas à raiz. Também não entram symlinks Git, submodules, `.env*`, arquivos usuais de credenciais/chaves/certificados e extensões binárias de artefatos. O snapshot persiste somente metadados derivados, nunca o conteúdo de source/manifests nem o alvo de links.

## Diff e replay

`RepositorySnapshotComparer` compara caminhos e object IDs Git de dois snapshots e produz listas ordenadas de arquivos adicionados, removidos e alterados. No replay, o AECS reconstrói a snapshot no commit-base isolado antes dos gates:

- hashes iguais e diff vazio permitem continuar a comparação de ambiente/candidato;
- hash ou inventário divergente produz `EnvironmentDivergence` quando o candidato permanece idêntico;
- o evento assinado registra hashes esperado/observado e o diff;
- evidências antigas sem snapshot continuam reproduzíveis pelo fluxo legado e não recebem um snapshot retroativo.

No Evidence Graph, o snapshot é um nó autenticado ligado ao TaskContract, à baseline, à execução e ao contexto compilado.
