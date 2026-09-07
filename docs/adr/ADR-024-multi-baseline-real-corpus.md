# ADR-024: Representar corpus real por checkout, baseline e oráculo de tarefa

- Status: Accepted
- Date: 2026-09-02

## Context

O protocolo `aecs.adaptive-offline-dataset/v1` congela um repositório e uma baseline para todas as
tarefas. Isso é adequado para um lote contemporâneo sobre o mesmo snapshot, mas não representa
corpora históricos de resolução de issues. Nesses corpora, cada instância possui seu próprio commit
anterior à solução e seu próprio oráculo de testes.

O inventário do AECS também não sustenta a amostra da #89: existem 15 TaskContracts versionados,
quatro sob `tasks/real-project`, 43 issues no GitHub e nenhuma coleção de 50 recomendações shadow
autenticadas. Duplicar contratos ou gerar mutações sintéticas não as transforma em tarefas reais.

Corpora públicos reforçam a necessidade. O
[SWE-bench](https://github.com/SWE-bench/SWE-bench/blob/main/docs/guides/datasets.md) registra
repositório, `base_commit`, test patch e testes fail-to-pass/pass-to-pass por instância. Seu
[conjunto Multilingual](https://www.swebench.com/multilingual.html) distribui 300 tarefas por 42
repositórios e não inclui C#.
[Microsoft InferredBugs](https://github.com/microsoft/InferredBugs) inclui snapshots C# before/after,
mas não um ambiente executável completo de repositório.
[RunBugRun](https://github.com/giganticode/run_bug_run) inclui C# executável, porém trabalha com
programas curtos e delega a licença às fontes originais.

Além da baseline, há um problema de identidade. A autorização do Evidence Graph inclui o path do
repositório. Um worktree efêmero recriado para cada execução mudaria esse path e impediria autenticar
de forma estável a recomendação shadow correspondente.

## Decision

Adicionar `aecs.adaptive-offline-dataset/v2`. O v1 permanece aceito para lotes single-baseline; o v2
é o protocolo para corpora históricos multi-baseline.

### Checkout externo estável

O manifesto v2 contém um catálogo de checkouts. Cada entrada registra ID, path relativo à raiz do
manifesto, origem HTTP(S), licença SPDX e autorização de redistribuição/uso. O checkout e o
manifesto operacional ficam fora do repositório AECS.

Cada tarefa referencia um checkout estável por `repositoryId`. Checkouts que representam baselines
diferentes usam paths diferentes, mesmo quando vieram da mesma origem. Isso mantém a identidade do
Evidence Graph estável entre a geração da recomendação e a execução pareada.

Antes de chamar o provider, o gate exige checkout limpo, `HEAD` idêntico à baseline registrada e
remote `origin` equivalente à origem pré-registrada.

### Baseline derivada e oráculo

Cada tarefa registra dois commits:

- `upstreamCommit`: estado original anterior à solução;
- `baselineCommit`: um único commit filho que adiciona somente o oráculo autorizado.

O gate recalcula `git diff --binary upstreamCommit baselineCommit`, valida seu SHA-256 e rejeita
merge commits ou parent divergente. Todo arquivo alterado por esse commit deve casar com
`oracle.allowedPaths`. Esses mesmos padrões precisam constar em `TaskContract.scope.forbidden`, de
modo que o Context Compiler e o Scope gate impeçam o agente de ler como alvo ou modificar o
oráculo.

O oráculo registra imagem Docker por digest, testes fail-to-pass e pass-to-pass. O TaskContract deve
usar runtime Docker e a mesma imagem. O comando configurado dentro do contrato continua sendo a
autoridade que executa o harness; os nomes de teste no manifesto são provenance e não substituem o
resultado verificável do gate.

O schema não possui campo de gold/solution patch. Como propriedades desconhecidas são rejeitadas,
incluir esse patch no manifesto operacional falha antes da execução. O processo de curadoria pode
usar a solução apenas em uma fase externa de validação e deve removê-la antes do pré-registro.

### Fingerprint, checkpoints e relatório

O fingerprint v2 cobre o catálogo, todos os commits, hashes, imagens, oráculos, hashes dos
TaskContracts, recomendações e política experimental. O loader recalcula cada hash de contrato.
Alterar qualquer um deles cria outro dataset ou falha antes da execução e invalida o resume anterior.

O relatório `aecs.adaptive-offline-report/v2` registra por par: `repositoryId`, origem, licença,
upstream, baseline derivada, hash do oráculo e imagem. Pares com checkout, provenance, evidência ou
preflight inválidos continuam no denominador e executam zero chamadas de provider.

## Alternatives considered

### Manter uma baseline única

Rejeitada para a #89. Exigiria 50 tarefas reais abertas no mesmo snapshot ou uma agregação
sintética que mudaria a natureza do experimento.

### Mover um único clone entre commits

Rejeitada. Isso altera o checkout fonte, quebra execução concorrente/retomável e torna instável o
path usado pela autorização das evidências.

### Criar worktrees efêmeros automaticamente

Rejeitada como identidade operacional primária. Worktrees continuam úteis dentro do pipeline para
isolar braços, mas a recomendação shadow precisa apontar para um checkout fonte estável e auditável.

### Versionar fixtures completas no AECS

Rejeitada. Aumentaria o repositório, misturaria licenças e poderia publicar código ou patches de
terceiros sem necessidade.

## Consequences

O v2 permite adaptar benchmarks reais sem incorporar seu conteúdo ao Git do AECS e torna explícita
a diferença entre commit upstream e baseline com testes. A proteção custa espaço em disco: uma
avaliação histórica pode precisar de dezenas de worktrees/checkouts estáveis.

O protocolo ainda não escolhe o corpus da #89. A curadoria deve confrontar licença, linguagem,
oráculo executável, custo de build e representatividade para o AECS. A existência de v2 não torna
InferredBugs, RunBugRun ou SWE-bench automaticamente adequados.

A #89 só pode pré-registrar depois que os 50 checkouts, contratos, oráculos e recomendações shadow
forem autenticados. A #35 permanece bloqueada e nenhum resultado v2 ativa roteamento operacional.
