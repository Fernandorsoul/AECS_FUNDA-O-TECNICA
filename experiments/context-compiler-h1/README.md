# H1 — Context Compiler A/B

Este benchmark pré-registra 30 pares (3 tarefas × 10 repetições). A variante de referência
usa somente a ordem ordinal dos caminhos; a candidata usa objetivo, símbolos e relações do
grafo Roslyn. O orçamento restritivo impede que todos os arquivos caibam no prompt. Modelo,
provider, seed, limites, parâmetros, tarefas e baseline são idênticos.

O diretório `repository` é um template versionado. Copie todo o experimento para fora deste
checkout, inicialize somente o repositório do dataset e mantenha os artefatos fora dele:

```powershell
$root = Join-Path $env:TEMP "aecs-h1-context"
Copy-Item experiments/context-compiler-h1 $root -Recurse
git -C "$root/repository" init
git -C "$root/repository" config user.email "experiment@aecs.local"
git -C "$root/repository" config user.name "AECS Experiment"
git -C "$root/repository" add .
git -C "$root/repository" commit -m "baseline"

dotnet run --project src/AECS.Cli/AECS.Cli.csproj -- experiment `
  --dataset "$root/dataset.cloud.json" `
  --output "$root-output" `
  --include-real-providers `
  --allow-host-execution
```

A chave vem de `OPENAI_API_KEY` ou `--cloud-key`; não pertence ao manifesto. Use um endpoint
OpenAI-compatible com telemetria de tokens. `report.json`, `results.csv`, `comparisons.csv`,
`analysis.csv`, checkpoints e evidências individuais formam o artefato reproduzível.

O analisador conclui `Maintain` apenas se o efeito mínimo pré-registrado for atingido e o
intervalo de confiança pareado de 95% excluir zero. Amostra incompleta, custo ausente/zero ou
incerteza remanescente concluem `Adjust`; falha, escopo ou efeito abaixo dos critérios de morte
concluem `Abandon`. Falhas nunca são removidas da amostra.

O workflow manual `Context Compiler H1` executa o mesmo protocolo com o secret
`AECS_EXPERIMENT_OPENAI_API_KEY` e publica output, evidências e a chave pública de verificação
por 30 dias. Ele nunca é acionado por push ou pull request.
