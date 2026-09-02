# Reexecução H1 local com Context Compiler v2 — 2026-09-02

## Resultado executivo

O protocolo corrigido concluiu 60/60 runs com o provider Ollama real no host de 16 GB. A variante
`graph-ranked-token-budget/v2` incluiu os arquivos `Policy` e `Contract` exigidos em 30/30 runs,
sempre nos dois primeiros ranks e antes de qualquer decoy. Os 30 pares tiveram contexto efetivamente
diferente.

A candidata compilada produziu 6/30 mudanças verificadas (20%) contra 0/30 da referência ordinal,
sem falhas, skips ou violações de escopo. Ela também consumiu em média 107,17 tokens a menos por par.
Esse é um sinal empírico favorável ao Context Compiler, mas o analisador manteve a conclusão H1 em
`Adjust`: como a referência teve zero VCC, a melhora relativa da métrica pré-registrada
VCC/custo fica matematicamente indefinida. O resultado não autoriza mudar o default nem decidir a
#35.

Rastreabilidade: [issue #84](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/84).

## Protocolo e identidade

| Item | Valor |
| --- | --- |
| Revisão executada | `0ec45c96d086ef77f1a9cde6c71c10eca5ef339f` |
| Base `dev` | `352664a` |
| Dataset completo | `context-compiler-h1-local-low-hardware` v2.0.0 |
| Hash completo | `sha256:654b38c9d26dbf3b0e3bf7086e4cbc948e77269c5d4a654c0e26d58a5435be3b` |
| Baseline completo | `560f442e9bc9f03f4ba3c448816e3c130f861b8f` |
| Dataset piloto | `context-compiler-h1-local-low-hardware-pilot` v2.0.0 |
| Hash piloto | `sha256:326bf80ad8748a0a30a782062fd2b207c37d7105530a8b44a938335853c2a741` |
| Baseline piloto | `3f4853d342a08f29115e7c0e37938133075529ff` |
| Design completo | 3 tarefas × 10 repetições × 2 variantes; 30 pares/60 runs |
| Referência | `naive-path-order-token-budget/v1` |
| Candidata | `graph-ranked-token-budget/v2` |
| Modelo/provider | `qwen2.5-coder:1.5b` / Ollama local |
| Modelo efetivo | digest `d7372fd82851…b64a0668`, GGUF Q4_K_M, 986.062.089 bytes |
| Seed | `4200` |
| Orçamento de contexto | 1600 tokens, 4000 caracteres e 200 tokens/400 caracteres por arquivo |
| Janela Ollama efetiva | 8192 tokens |
| Métrica primária | mudanças verificadas por custo computacional local estimado |

O primeiro disparo do piloto parou antes do provider: o executor compilava `Release`, mas
`dotnet run --no-build` selecionava implicitamente um binário `Debug` antigo. A revisão executada
fixa `--configuration Release`, compila e executa o mesmo projeto/configuração e possui teste de
regressão. A tentativa sem inferência não integra a amostra publicada.

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
| SDK resolvido pelos verificadores host | `10.0.400-preview.0.26322.102` |

A fixture isolada não contém o `global.json` do AECS. Por isso, os comandos `dotnet` declarados no
TaskContract resolveram o SDK 10 preview disponível no `PATH`, embora o controlador tenha sido
iniciado pelo SDK 9 portátil. O mesmo tooling foi usado nos dois lados de todos os pares; ainda
assim, essa diferença é um confound de reprodução que deve ser eliminada antes de ampliar o corpus.

## Gate piloto

O piloto concluiu 4/4 runs, sem falha ou skip, com 0 mudanças verificadas e 4 rejeições. Nos dois
runs compilados, o contexto foi exatamente `RetentionPolicy.cs` no rank 1 e
`RetentionContract.cs` no rank 2. Seus hashes foram, respectivamente,
`sha256:6e9bc3e364f91dab41b722b69636863290820796935e47ed382ca94e8aebd624` e
`sha256:7d05d67168a37afa611b5264325e7adde54024864c04690bbf61eca883076db0`.

Parsing, chamada ao provider, aplicação, isolamento, verificação, persistência, assinatura,
contabilidade e relatório v4 funcionaram. A RAM livre mínima foi 1.254.334.464 bytes, acima do piso
operacional de 1 GiB, então o protocolo autorizou a amostra completa.

## Integridade do contexto

Os manifests autenticados mostram três seleções determinísticas na candidata:

| Tarefa | Rank 1 | Rank 2 | Runs conformes |
| --- | --- | --- | ---: |
| Invoice | `InvoicePolicy.cs` | `InvoiceContract.cs` | 10/10 |
| Queue | `QueueContract.cs` | `QueuePolicy.cs` | 10/10 |
| Retention | `RetentionPolicy.cs` | `RetentionContract.cs` | 10/10 |

A ordem relativa de Queue decorre do ranking semântico, mas ambos os arquivos obrigatórios ocupam os
dois primeiros lugares. Não houve arquivo obrigatório ausente nem decoy à frente deles. A referência
incluiu `AccountArchive.cs`, `AddressBook.cs` e `AuditClock.cs` em todos os 30 runs. Portanto, os 30
pares registraram `effectiveContextChanged=true` e o experimento corrige a contaminação documentada
na #80.

## Amostra completa

| Métrica | `naive-local` | `compiled-local` |
| --- | ---: | ---: |
| Runs concluídos | 30/30 | 30/30 |
| Mudanças verificadas | 0 | 6 |
| Primeira passagem | 0% | 20% |
| Falhas/skips | 0/0 | 0/0 |
| Violações de escopo | 0 | 0 |
| Tokens, média | 886,50 | 779,33 |
| Tokens, mediana | 861,50 | 794,00 |
| Latência, média | 10,020 s | 9,673 s |
| Latência, mediana | 9,852 s | 8,575 s |
| Custo local estimado | USD 0,0030129 | USD 0,0026377 |
| VCC/USD estimado | 0 | 2274,70 |

As seis verificações da candidata se distribuíram em Invoice (2/10), Queue (4/10) e Retention
(0/10). Entre as 54 rejeições, 33 falharam orçamento mais critério de aceite e 21 falharam build mais
critério de aceite. Os agentes completaram todas as tentativas e o sistema rejeitou os candidatos
inválidos de forma governada; rejeição de código não foi tratada como falha de infraestrutura.

No pareamento, a candidata venceu 6 vezes, a referência nenhuma e houve 24 empates. A diferença
média de VCC foi 0,20 por par, com IC 95% de 0,0483 a 0,3517. A diferença média de tokens foi
-107,17, com IC 95% de -131,97 a -82,37. A diferença média de latência foi -0,347 segundo, mas o
IC 95% de -1,908 a 1,213 inclui zero. O custo total efetivo foi USD 0,0056506 e o CPVC agregado foi
USD 0,0009418; são estimativas da política `aecs.local-compute-cost/v1`, não medição elétrica.

O relatório `aecs.experiment-report/v4` concluiu `Adjust`. O texto genérico do analisador menciona
custo ausente ou não positivo, porém 30/30 pares tinham custo positivo nos dois lados. A condição
real que impediu `Maintain` foi `relativePrimaryMetricImprovement=null`, causada pelo VCC zero da
referência. Esse caso-limite da métrica deve ser tratado explicitamente antes do experimento de 50
tarefas.

## Recursos do host

A coleta completa levou 703,78 segundos de parede e produziu 28 amostras. A CPU teve média de 39% e
pico de 76%. A memória física livre teve média de 1.866.532.133 bytes (1,74 GiB) e mínimo de
1.341.923.328 bytes (1,25 GiB), sem violar o piso de 1 GiB. O maior engine de GPU amostrado chegou a
95,2%, e a memória dedicada de processo chegou a 1.589.211.136 bytes.

Ao fim da coleta, o Ollama informou contexto 8192 e 1.354.456.104 bytes residentes integralmente em
VRAM. Isso demonstra viabilidade desta carga no host, com folga de RAM pequena; não demonstra que
modelos maiores sejam seguros.

## Evidência, retomada e publicação

O store autenticado releu as 60 evidências com código 0, uma única autoridade
`sha256:f9177ffec85b6cef3b47e214a6f904cf7126670a3857ede7b6dc147dcd35c21c` e zero diagnósticos.
A publicação inclui somente
`f9177ffec85b6cef3b47e214a6f904cf7126670a3857ede7b6dc147dcd35c21c.public.pem`; a chave privada
foi excluída.

Depois de descarregar o modelo, `--resume` foi executado sobre a amostra completa. Havia zero modelos
carregados antes e depois, e os 60 runs mais as 60 evidências preservaram os mesmos hashes SHA-256.
O piloto repetiu a prova sobre seus oito arquivos. O código 1 é esperado porque
`report.succeeded=false` para a conclusão `Adjust`; nenhuma nova inferência ocorreu.

O pacote público contém datasets, fixture sem `.git`, piloto, relatório v4, CSVs, 64 runs, 64
evidências, telemetria, logs, provas de retomada, verificações autenticadas, chaves públicas e
manifesto:

- release: [Context Compiler H1 local v2 — 2026-09-02](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/releases/tag/context-compiler-h1-local-v2-2026-09-02);
- arquivo: `aecs-h1-local-context-v2-2026-09-02-artifacts.zip`;
- SHA-256 do arquivo: `d8341773daa17712f34a2f7fd783efd15d16bd79128b110b9dd1b91fe8d0edf0`;
- SHA-256 do relatório completo: `fdccb4452b2129e75d02b9ecf257a73630fa83804b4cd40e76b06b37015def77`.

O ZIP foi expandido novamente antes da publicação. As 204 entradas do manifesto passaram, as
contagens foram 60/60 na amostra completa e 4/4 no piloto, e nenhuma chave privada foi encontrada.

## Conclusão e próximo gate

O resultado substitui a avaliação local invalidada da #74 para a finalidade de comparar contexto.
O AECS consegue executar, governar, verificar, autenticar e retomar uma experiência real com IA
local neste hardware. O Context Compiler v2 seleciona o contexto correto e apresentou ganho de
qualidade e redução de tokens neste corpus sintético pequeno.

Ainda não há base para declarar o produto pronto nem para decidir a #35. Antes das 50 tarefas reais,
é necessário corrigir ou redefinir de forma pré-registrada a análise quando o baseline tem zero VCC,
fixar o SDK usado pelos verificadores e desenhar o corpus externo sem ajustar critérios depois dos
resultados. O H1 cloud da #72 continua independente e bloqueado pela ausência de credencial.
