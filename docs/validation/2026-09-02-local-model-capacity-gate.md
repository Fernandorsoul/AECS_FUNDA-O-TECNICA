# Gate de capacidade de modelos locais — 2026-09-02

## Resultado executivo

Nenhum novo modelo foi aprovado para o hardware alvo. `qwen2.5-coder:3b` executou os seis runs
previstos sem falha de infraestrutura, mas produziu 0/6 mudanças verificadas e reduziu a RAM física
livre a 496.959.488 bytes, abaixo do piso pré-registrado de 1 GiB. O gate determinou
`stop-and-diagnose`.

O estágio `qwen2.5-coder:7b` não foi baixado nem executado. Essa interrupção não é uma desistência
arbitrária: a política congelada antes do download manda avançar após falha apenas de qualidade e
parar após falha operacional/de segurança de recurso. Executar um modelo oficial de 4,7 GB depois de
o 3B violar o piso de memória contrariaria o protocolo e aumentaria o risco de pressão severa ou OOM.

Rastreabilidade: [issue #77](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/77).

## Pré-registro e escada

O commit `79e7092` versionou os dois datasets e `local-model-capacity-gate.json` antes da instalação
de qualquer candidato novo. `ollama list` continha somente o 1.5B nesse momento.

| Ordem | Modelo | Tamanho oficial | Estado |
| ---: | --- | ---: | --- |
| 1 | `qwen2.5-coder:3b` | 1,9 GB | executado e reprovado |
| 2 | `qwen2.5-coder:7b` | 4,7 GB | não baixado; bloqueado pelo gate de recurso |

O protocolo não é H1: usa somente `graph-ranked`, sem comparar estratégias de contexto. Cada estágio
contém as três tarefas simples da fixture, duas repetições, seed 5200, seis runs, orçamento de contexto
de 1600 tokens e janela Ollama de 8192 tokens.

Um modelo só passaria com 6/6 runs concluídos, pelo menos 3/6 mudanças verificadas, ao menos uma por
tarefa, zero violações de escopo, seis evidências autenticadas, checkout original preservado, nenhum
timeout/OOM, no mínimo 1 GiB de RAM física livre e `--resume` sem nova inferência.

O [catálogo oficial do Ollama](https://ollama.com/library/qwen2.5-coder) foi usado somente para fixar
tags e tamanhos esperados. Identidade efetivamente observada do estágio executado:

| Campo | Valor |
| --- | --- |
| Modelo | `qwen2.5-coder:3b` |
| Digest | `sha256:f72c60cabf6237b07f6e632b2c48d533cef25eda2efbd34bed21c5e9c01e6225` |
| Artefato local | 1.929.912.626 bytes |
| Formato | GGUF, qwen2, 3,1B, Q4_K_M |
| Ollama | `0.33.2` |
| Contexto efetivo | 8192 tokens |
| Modelo residente | 2.310.358.957 bytes, integralmente em VRAM segundo `/api/ps` |

O host foi o mesmo perfil da #74: Windows 11 Pro `10.0.26200`, Ryzen 5 4500 com 6 cores/12
threads, 17.064.321.024 bytes de RAM e Radeon RX 570 com 4.293.918.720 bytes. O controlador foi
iniciado pelo SDK portátil .NET `9.0.317` e runtime `Microsoft.NETCore.App 8.0.30`. Como a fixture
isolada não leva o `global.json` do AECS, os verificadores host registraram o SDK
`10.0.400-preview.0.26322.102`; esse confound de tooling permanece explícito.

## Resultado do 3B

| Métrica | Observado | Exigido | Passou |
| --- | ---: | ---: | :---: |
| Runs concluídos | 6/6 | 6/6 | sim |
| Falhas/skips | 0/0 | 0/0 | sim |
| Mudanças verificadas | 0/6 | ≥3/6 | **não** |
| Tarefas com ao menos uma verificada | 0/3 | 3/3 | **não** |
| Violações de escopo | 0 | 0 | sim |
| Evidências autenticadas | 6 | 6 | sim |
| Diagnósticos de autenticação | 0 | 0 | sim |
| RAM livre mínima | 496.959.488 bytes | ≥1.073.741.824 | **não** |
| Timeout/OOM | 0 | 0 | sim |
| Checkout original alterado | 0 | 0 | sim |
| Retomada sem inferência | sim | sim | sim |

Todos os agentes retornaram sucesso, todas as aplicações e verificações de escopo passaram, mas os
seis critérios de aceite falharam. Dois candidatos não produziram mudança; dois excederam o limite de
arquivos; dois chegaram ao build e falharam. Os demais builds foram bloqueados corretamente depois de
uma falha de fronteira anterior. Isso separa disponibilidade do provider de competência para editar
o código.

O relatório `aecs.experiment-report/v3` registrou 4.330 tokens de entrada, 486 de saída, mediana de
latência de 12,30 segundos, duração agregada de 121,56 segundos e custo local estimado de
USD 0,0019351. O custo segue `aecs.local-compute-cost/v1` e não representa fatura nem medição
elétrica.

## Recursos observados

As cinco amostras de telemetria cobriram 144,76 segundos de parede. CPU média foi 30,2%, com pico de
41%. RAM livre média foi 1.620.244.890 bytes (1,51 GiB); três amostras ficaram abaixo de 1 GiB e o
mínimo foi 496.959.488 bytes (0,46 GiB). A memória dedicada de processo chegou a 2.327.740.416 bytes.

O maior engine de GPU amostrado teve média de 4,4% e pico de 9,1%, enquanto `ollama ps` informou o
modelo `100% GPU`. Os dados não são contraditórios: o primeiro é utilização pontual de engines; o
segundo é localização da alocação do modelo. A baixa folga de RAM continua relevante mesmo com os
pesos residentes na GPU.

O valor mínimo pode sofrer influência de outros processos do Windows. Pelo desenho pré-registrado,
isso é uma limitação a registrar, não uma licença para ignorar o gate após observar o resultado.

## Evidência, retomada e artefatos

O store autenticado leu as seis evidências com uma autoridade e zero diagnósticos. A chave pública é
`21f1c4510c8c8129f31fb1ca4232f4c33d2dab3665fc20e230a484f3b5511a6b.public.pem`; a chave privada
foi excluída da publicação.

Após descarregar o 3B, `--resume` encontrou zero modelos carregados antes e depois, preservou os 12
arquivos de run/evidência e todos os hashes SHA-256. O código 1 permaneceu coerente com
`report.succeeded=false` e nenhuma nova inferência ocorreu.

O pacote publicado contém datasets, regra do gate, relatório v3, CSVs, seis runs, seis evidências,
telemetria, avaliação, logs, chave pública e manifesto:

- release: [Local model capacity gate — 2026-09-02](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/releases/tag/local-model-capacity-gate-2026-09-02);
- arquivo: `aecs-local-model-capacity-gate-2026-09-02-artifacts.zip`;
- SHA-256: `aa24b7aa9a7ada3fdbc1b900f3d1a26d592df8966b764275061ffea6e69dcea2`;
- SHA-256 de `report.json`: `58532fdd80ad06a7e4f431e5d70ed524b3f659b7f2b504932eb6c372552abb4a`;
- dataset canônico: `sha256:3c1736ced410a72e1d861efceb49fe727fb52e6d99777b022142e0452c1c0f2f`.

O ZIP foi expandido antes da publicação: 32/32 entradas passaram pelo manifesto, as contagens foram
6 runs e 6 evidências e nenhuma chave privada foi encontrada.

## Confronto com o código real e decisão

A ADR-005 originalmente propunha modelos de 1,3B–3B para R0/R1. O código atual, porém, fixa 7B para
R0/R1 e 14B para R2–R4. A #74 já havia reprovado o 1.5B em qualidade; este gate reprova o 3B em
qualidade e margem de memória. Como o 7B não pôde ser executado com segurança, nenhum desses valores
hard-coded ganhou validação empírica no host alvo.

Consequentemente, `ExecutionController` não foi alterado. O resultado impede apresentar o roteamento
7B/14B como suportado nesse computador e também impede avançar diretamente às 50 tarefas. O próximo
passo coerente é diagnosticar e controlar a pressão de memória — inclusive processos concorrentes,
janela/KV cache e política de offload — em uma nova issue e novo pré-registro. O H1 cloud da #72 e a
decisão da #35 continuam separados e pendentes.
