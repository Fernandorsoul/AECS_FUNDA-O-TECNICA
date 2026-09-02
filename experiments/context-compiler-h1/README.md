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

## Replicação local em hardware baixo

`dataset.local-low-hardware.json` é um pré-registro separado. Ele não altera, substitui nem pode ser
combinado com o resultado cloud. O protocolo mantém as mesmas três tarefas, estratégias, limites de
contexto, seeds, 10 repetições e 30 pares, mas fixa o provider `Local`, o modelo
`qwen2.5-coder:1.5b`, o Ollama loopback e uma janela de 8192 tokens.

Host registrado antes da coleta, em 2026-09-02: Windows 11 `10.0.26200`, Ryzen 5 4500 com 6 cores
e 12 threads, 16 GB de RAM e Radeon RX 570 com 4 GB. Ollama `0.33.2` estava ativo e ainda não
possuía modelos instalados. O digest efetivamente baixado e o processador usado pelo Ollama devem
ser registrados no relatório, sem pressupor aceleração da GPU.

`dataset.local-low-hardware-pilot.json` executa apenas Retention em dois pares. Ele é um gate
operacional anterior à amostra: valida disponibilidade do modelo, parsing, isolamento, persistência
de evidência e contabilidade. Decisão rejeitada, resposta malformada ou baixa qualidade são resultados
do modelo e não autorizam ajustar o dataset completo. Somente uma falha de infraestrutura impede a
coleta até que sua causa seja corrigida.

A estimativa de custo usa `aecs.local-compute-cost/v1`: 200 W, USD 0,20/kWh, hardware de USD 600
e vida útil de 10.000 horas. São hipóteses reproduzíveis do adapter, não medição elétrica nem
fatura. CPU, memória e GPU observáveis devem ser registrados separadamente durante a execução.
