# Replicação H1 local em hardware baixo — 2026-09-02

## Resultado executivo

O AECS executou o A/B pareado completo com um provider Ollama real no host de 16 GB: 60/60 runs
terminaram, sem falha ou skip, com 60 evidências autenticadas e janela efetiva de 8192 tokens. Isso
valida o caminho operacional para IA local nesse hardware.

O modelo `qwen2.5-coder:1.5b` não atingiu qualidade útil neste benchmark: 0/60 candidatos foram
verificados. Tanto o contexto ordinal quanto o contexto compilado tiveram VCC igual a zero; portanto,
H1 local terminou `Adjust` e não permite atribuir ganho de correção ao Context Compiler. O resultado
não substitui o H1 cloud da [issue #72](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/72)
e não habilita a decisão da #35.

Rastreabilidade: [issue #74](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/74).

## Protocolo congelado

O pré-registro entrou no commit `f0cb8e4`, antes de qualquer chamada experimental do modelo. O
adapter passou a transmitir `num_ctx` explicitamente no commit `4826a12`; a primeira coleta que
revelou a omissão foi invalidada e não integra os resultados abaixo.

| Item | Valor |
| --- | --- |
| Dataset | `context-compiler-h1-local-low-hardware` v1.0.0 |
| Hash canônico | `sha256:bd43f5b82b5abe4935e989e939058ffcaa11b38e43a5555ef8bdfc127707a38e` |
| Design | A/B pareado, 3 tarefas × 10 repetições × 2 variantes |
| Referência | `naive-path-order` |
| Candidata | `graph-ranked` |
| Modelo/provider | `qwen2.5-coder:1.5b` / `Local` |
| Seed | `4200` |
| Contexto | máximo de 1600 tokens, 4000 caracteres e janela Ollama de 8192 tokens |
| Métrica primária | mudanças verificadas por custo computacional local estimado |
| Baseline isolada | `c74b42b318071c57bdd39f27f97a6b25ec6ed4d3` |

O [modelo oficial no Ollama](https://ollama.com/library/qwen2.5-coder) ocupa 986.062.089 bytes em
disco. O digest observado foi
`sha256:d7372fd828518a4d38b1eb196c673c31a85f2ed302b3d1e406c4c2d1b64a0668`, no formato GGUF,
família qwen2, 1,5 bilhão de parâmetros e quantização Q4_K_M.

## Ambiente efetivo

| Componente | Identidade observada |
| --- | --- |
| Sistema | Windows 11 Pro `10.0.26200`, `win-x64` |
| CPU | AMD Ryzen 5 4500, 6 cores/12 threads |
| RAM | 17.064.321.024 bytes (16 GB nominais) |
| GPU | Radeon RX 570, 4.293.918.720 bytes, driver `31.0.21924.61` |
| Ollama | `0.33.2` |
| SDK do controlador | .NET SDK `9.0.317` |
| Runtime da CLI | `Microsoft.NETCore.App 8.0.30` |
| SDK encontrado pelos verificadores host | `10.0.400-preview.0.26322.102` |

A CLI foi iniciada pelo SDK portátil 9. O repositório experimental isolado não contém o
`global.json` do AECS e os processos host de verificação resolveram o SDK 10 preview instalado no
PATH. Essa diferença está registrada como limitação de reprodução; não houve erro de infraestrutura,
mas uma repetição futura deve tornar o SDK dos verificadores explícito.

Ao fim da coleta, a API `/api/ps` informou contexto 8192, 1.354.456.104 bytes residentes e o mesmo
volume em VRAM; `ollama ps` apresentou `100% GPU`. Isso descreve a alocação do modelo, não uma média
de utilização durante toda a execução.

## Gate piloto

O piloto corrigido usou o dataset canônico
`sha256:6b24ce3341f3d18436095b32df1c87324c5c3d3aa6b467f0f9700d880ecf34c9` e terminou os quatro
runs previstos em 63,6 segundos de duração agregada: 4 concluídos, 0 falhos, 0 ignorados, 0
verificados e 4 rejeitados. Parsing, aplicação, isolamento, orçamento, persistência, assinatura e
contabilidade funcionaram; a baixa qualidade foi tratada como resultado do modelo, não como motivo
para alterar a amostra.

## Amostra completa

| Métrica | `naive-local` | `compiled-local` |
| --- | ---: | ---: |
| Runs concluídos | 30/30 | 30/30 |
| Mudanças verificadas | 0 | 0 |
| Falhas/skips do run | 0/0 | 0/0 |
| Violações de escopo | 0 | 0 |
| Mediana de tokens | 862 | 831 |
| Média de tokens | 880,07 | 823,67 |
| Mediana de latência | 10,46 s | 10,19 s |
| Média de latência | 10,34 s | 10,42 s |
| Custo local estimado | USD 0,0029295 | USD 0,0026606 |
| VCC/USD estimado | 0 | 0 |

Os 30 pares ficaram completos. A candidata usou em média 56,4 tokens a menos por par; o intervalo
de confiança de 95% foi de -77,80 a -35,00 tokens. A diferença média de latência foi 0,078 segundo,
com intervalo de -0,366 a 0,521 segundo. A diferença de custo estimado também incluiu zero. Como
nenhuma variante produziu VCC, a melhora relativa da métrica primária é indefinida.

Os 60 agentes retornaram sucesso e os 60 candidatos passaram pela aplicação e pelo escopo. Porém,
todos falharam o critério de aceite; 30 excederam o limite de arquivos, 29 dos demais falharam o build
e um não produziu mudança. Os 31 builds restantes foram corretamente bloqueados após uma falha de
fronteira anterior. O resultado evidencia que o adapter local funciona, enquanto esse modelo pequeno
não seleciona e edita o código com precisão suficiente.

O relatório `aecs.experiment-report/v3` registrou duração agregada de 622,64 segundos e custo local
estimado total de USD 0,0055901. Esse custo usa a política `aecs.local-compute-cost/v1` — 200 W,
USD 0,20/kWh, hardware de USD 600 e vida de 10.000 horas — e não é medição elétrica nem fatura.

## Telemetria do host

A execução de parede levou 725,85 segundos, incluindo a própria coleta de 34 amostras. A CPU teve
média de 43,3%, mínimo de 18% e pico de 65%. A memória física livre teve média de 2.008.034.846 bytes
(1,87 GiB) e mínimo de 1.622.241.280 bytes (1,51 GiB). O maior engine de GPU em cada amostra teve
média de 31,9% e pico de 99,4%; o maior valor de memória dedicada de processo foi 1.983.225.856 bytes
(1,85 GiB). O contador de GPU é o máximo entre engines, não utilização global da placa.

Esses números mostram que o host conclui a carga sem esgotamento, mas com pouca folga de RAM. Não
demonstram que modelos maiores caberão, nem medem energia real.

## Integridade e retomada

A leitura das 60 evidências pelo store autenticado terminou com código 0, uma única autoridade e zero
diagnósticos. A chave pública é
`4123d9cdf0fa6017e5ff0e6b649408d9d018f53643e161530e79371eb715e081.public.pem`; a chave privada
foi excluída do pacote.

Depois da coleta, o modelo foi descarregado e `--resume` foi executado sobre os mesmos artefatos.
Havia zero modelos carregados antes e depois. Os 60 runs e as 60 evidências mantiveram exatamente os
mesmos hashes SHA-256; nenhum arquivo foi criado ou removido. O código 1 é o contrato esperado da CLI
para `report.succeeded=false`, e não uma falha de retomada.

O pacote publicado contém piloto, relatório v3, seis CSVs, 60 runs persistidos, 60 evidências,
telemetria, logs, verificação, chave pública e manifesto com 154 entradas:

- release: [Context Compiler H1 local — 2026-09-02](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/releases/tag/context-compiler-h1-local-2026-09-02);
- arquivo: `aecs-h1-local-low-hardware-2026-09-02-artifacts.zip`;
- SHA-256 do arquivo: `8bc3ea79fcf32eaf9d618a7741faa6f76bef35372aa425e0568dbe619bc643e3`;
- SHA-256 de `report.json`: `a4c66d865f1cd4ac4b6931cf4ccce0fad7eed1dd8dcc7020d459749a97698c72`.

O ZIP foi expandido novamente antes da publicação: as 154 entradas do manifesto passaram, as
contagens foram 60/60 na amostra e 4/4 no piloto, e nenhuma chave privada foi encontrada.

## Conclusão e próximos passos

O AECS já consegue usar uma IA local real nesse hardware para percorrer todo o pipeline governado,
registrar custo, produzir candidatos, rejeitá-los com evidência e retomar de forma idempotente. Ele
ainda não consegue, com `qwen2.5-coder:1.5b`, produzir mudanças confiáveis nessas tarefas.

Assim, a replicação local satisfaz seu objetivo operacional, mas não confirma H1. A ordem coerente
permanece: executar separadamente o H1 cloud quando houver secret; definir um gate de capacidade para
modelos locais maiores que caibam no hardware; ampliar depois para tarefas reais e medir o hardware
alvo; somente então decidir a #35. A primeira coleta com contexto 32768 é apenas diagnóstico
invalidado e foi deliberadamente excluída da publicação.
