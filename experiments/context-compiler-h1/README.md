# H1 — Context Compiler A/B

Este benchmark pré-registra 30 pares (3 tarefas × 10 repetições). A variante de referência
usa somente a ordem ordinal dos caminhos; a candidata usa objetivo, símbolos e relações do
grafo Roslyn. O orçamento restritivo impede que todos os arquivos caibam no prompt. Modelo,
provider, seed, limites, parâmetros, tarefas e baseline são idênticos.

Os datasets atuais usam `aecs.experiment-dataset/v3` e declaram `requiredContextPaths` por tarefa.
Depois de compilar a variante graph-ranked, o pipeline exige esses arquivos antes de chamar o
provider. A estratégia vigente é `graph-ranked-token-budget/v2`, que não projeta relações
estruturais de namespace como dependências entre arquivos. Os datasets e resultados v1 publicados
em 2026-09-02 são históricos e possuem errata vinculada à issue #80.

Eles também preservam a política histórica conservadora para baseline com zero VCC: a melhora
relativa fica indefinida e H1 termina `Adjust`. Novos corpora, inclusive a futura expansão para 50
tarefas, devem usar `aecs.experiment-dataset/v4` e pré-registrar `zeroReferencePolicy` antes de
qualquer chamada ao provider. Consulte a documentação do harness para as regras válidas.

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

O executor abaixo cria um repositório isolado e novo, mantém output, evidências e chaves fora do
checkout, coleta telemetria do host e valida as contagens e a janela efetivamente carregada pelo
Ollama. Ele compila e executa explicitamente a configuração `Release`, impedindo que `--no-build`
reutilize um binário Debug antigo. O destino deve ser um caminho inexistente:

O parâmetro `-DotnetPath` aceita o executável de uma instalação portátil com SDK 9 estável e
runtime 8. O executor valida essa instalação antes do build e das chamadas ao provider, propaga
seu diretório pelo PATH aos processos filhos e fixa o SDK exato em um `global.json` commitado
na fixture isolada. Assim os worktrees de verificação usam a mesma versão do controlador,
mesmo quando o host possui SDKs mais novos. O ambiente do chamador é restaurado também em falhas.
`toolchain.json` e a telemetria registram o executável, SDK e runtimes selecionados. Essa preparação
gera uma nova baseline experimental; resultados anteriores mantêm sua identidade original.

```powershell
./experiments/context-compiler-h1/Invoke-LocalLowHardwareH1.ps1 `
  -Mode pilot `
  -ArtifactRoot "$env:LOCALAPPDATA/Temp/aecs-h1-local-pilot"

./experiments/context-compiler-h1/Invoke-LocalLowHardwareH1.ps1 `
  -Mode full `
  -ArtifactRoot "$env:LOCALAPPDATA/Temp/aecs-h1-local-full"
```

Uma conclusão experimental rejeitada faz a CLI retornar código 1 mesmo quando toda a amostra foi
coletada corretamente. O executor distingue esse resultado de falha operacional confrontando o
código com `report.succeeded` e exigindo todos os runs e todas as evidências esperadas.
Depois de validar esses artefatos, o próprio executor devolve o mesmo código ao chamador: `0` para
`Maintain` e `1` para `Adjust` ou `Abandon`.

## Gate de capacidade de modelos locais

`local-model-capacity-gate.json` é um pré-registro operacional separado de H1. Ele usa somente
`graph-ranked` e mede se existe um modelo local mínimo capaz de produzir mudanças verificadas no
host registrado. Portanto, seus resultados não comparam estratégias nem podem confirmar H1.

A escada testa `qwen2.5-coder:3b` e só avança para `qwen2.5-coder:7b` se o primeiro falhar em
qualidade. Cada dataset executa as três tarefas em duas repetições. Um modelo passa apenas com 6/6
runs completos, ao menos 3/6 mudanças verificadas, ao menos uma por tarefa, zero violações de
escopo, seis evidências, janela de 8192 tokens, no mínimo 1 GiB de RAM física livre e retomada sem
nova inferência. Falha operacional interrompe a escada para diagnóstico; ela não autoriza pular ao
modelo seguinte.

Depois de instalar o modelo do estágio, o mesmo executor aceita o dataset explícito:

```powershell
./experiments/context-compiler-h1/Invoke-LocalLowHardwareH1.ps1 `
  -DatasetFile dataset.local-capacity-3b.json `
  -ArtifactRoot "$env:LOCALAPPDATA/Temp/aecs-capacity-3b"
```
