# Alertas proativos calibrados

## Invariantes

Alertas são opt-in e derivados apenas de execuções encontradas pelo Evidence Graph com escopo de
repositório e carregadas novamente pelo store autenticado. Eles não são uma nova fonte de verdade:
o registro local ajuda entrega, deduplicação e métricas, mas elegibilidade, revisão e promoção
continuam no backend AECS.

Ausência de resposta nunca significa aprovação. `Read`, `Actioned` e `Expired` são estados de
atenção operacional e todos os eventos de lifecycle declaram `grantsApproval: false`. A ação
recomendada pode apontar para o serviço de revisão, mas o alerta não aprova nem aplica um patch.

## Ativação

Copie `aecs.alert-policy.example.json`, mantenha o arquivo local fora do versionamento quando ele
contiver destinatários específicos e altere `enabled` para `true`. Inicie o Jarvis explicitamente:

```powershell
dotnet run --project src/AECS.Cli/AECS.Cli.csproj -- jarvis `
  --repo C:\repositorio `
  --alert-policy C:\config\aecs.alert-policy.local.json `
  --alert-root C:\aecs-local\alerts
```

`AECS_ALERT_POLICY` pode fornecer o caminho da política e `AECS_ALERT_PATH` pode definir o root
local. Flags têm precedência sobre essas variáveis. Sem política, nenhuma avaliação ou entrega é
feita. Uma política carregada com `enabled: false` também não consulta evidências nem chama o sink.

Depois de `run` ou `experiment`, o Jarvis avalia automaticamente a janela autenticada. O operador
também pode controlar o ciclo pelo REPL:

```text
aecs> alerts evaluate
aecs> alerts list --json
aecs> alerts read <alert-id>
aecs> alerts act <alert-id> ticket/AECS-123
aecs> alerts metrics
```

## Política `aecs.alert-policy/v1`

| Campo | Regra |
| --- | --- |
| `id` | Identidade estável da política, usada na deduplicação e métricas |
| `enabled` | Opt-in obrigatório |
| `recipient` | Destinatário lógico do canal |
| `cooldownMinutes` | Janela desde a última tentativa de entrega que agrega ocorrências equivalentes |
| `lookbackHours` | Janela máxima consultada no Evidence Graph |
| `repeatedFailureThreshold` | Falhas de tentativa necessárias para o evento repetido |
| `events` | Allowlist dos seis tipos notificáveis |
| `severityOverrides` | Calibração explícita por tipo |
| `deadlineMinutes` | Prazo por tipo; o default cobre tipos omitidos |
| `channelAuthorization` | Autorizações explícitas de código/segredo |
| `deathCriteria` | Amostra mínima, ação mínima e máximo de alertas ignorados |

O parser recusa versão ou campos desconhecidos, propriedades JSON duplicadas, eventos repetidos,
taxas fora de `0..1` e janelas fora dos limites documentados. O exemplo fica desabilitado para
impedir ativação acidental.

## Eventos notificáveis

| Evento | Derivação autenticada | Severidade padrão | Ação recomendada |
| --- | --- | --- | --- |
| `Security` | `SecurityScan` do candidato em `Fail`/`Error` | `Warning` a `Critical`, conforme gate | Inspecionar evidência e remediar/documentar |
| `Scope` | gate `Scope` falhou ou estado `ScopeViolation` | `High` | Corrigir contrato/candidato sem ampliar política implicitamente |
| `Budget` | budget esgotado, estado ou falha `BudgetExceeded` | `High` | Inspecionar consumo antes de alterar limites |
| `RepeatedFailure` | tentativas malsucedidas atingem o threshold | `Warning`, escalando para `High` | Interromper retries e investigar a classe da falha |
| `Regression` | gate passou na baseline e falhou no candidato | `High` | Corrigir antes de promoção |
| `HumanReview` | decisão/estado `HumanReviewRequired` | `Warning` | Revisar diff e gates pelo serviço controlado |

Uma política pode substituir severidade e prazo sem alterar as condições que o backend deriva da
evidência.

## Deduplicação, cooldown e escalada

A chave SHA-256 combina política, task, tipo e assunto estável do evento. Nova ocorrência dentro do
cooldown da última tentativa atualiza `latestEvidenceId`, mantém todos os IDs em `evidenceIds`, incrementa
`occurrenceCount` e registra `Deduplicated`, sem chamar o sink novamente.

Se a severidade aumentar dentro da janela, o mesmo alerta registra `Escalated`, recebe novo prazo e
é entregue novamente. Alertas `Actioned` ou `Expired` não suprimem uma recorrência: ela cria um novo
registro. Depois do cooldown, outra ocorrência também cria um alerta independente.

## Conteúdo e privacidade

O envelope `aecs.alert-delivery/v1` contém somente tipo, severidade, recipient, título e resumo
templados, ação recomendada, prazo e `aecs://evidence/<id>`. Ele não copia diff, código, mensagens
brutas de verificadores, caminhos de achados, stdout/stderr ou valores de segredo. Os flags
`containsCode` e `containsSecrets` permanecem falsos.

Antes de entregar, o serviço combina a autorização da política com as capacidades declaradas pelo
sink. O sink local recusa código e segredos mesmo quando a política os permitir. Integrações futuras
implementam `IProactiveAlertSink`; declarar capacidade não elimina a necessidade de opt-in na
política.

## Sink e persistência local

`LocalJsonProactiveAlertSink` grava um envelope por entrega em `<alert-root>/inbox`. O
`JsonProactiveAlertStore` mantém o lifecycle por repositório em arquivo separado, com escrita
atômica e isolamento que proíbe armazenar dentro do repositório-alvo. O root padrão é
`%LOCALAPPDATA%/AECS/alerts` (ou equivalente da plataforma).

O estado local não é assinado. Adulterá-lo pode prejudicar deduplicação e métricas, mas não altera
a evidência, não concede aprovação e não torna um candidato promovível. O Evidence Graph e o store
autenticado são consultados novamente a cada avaliação.

## Lifecycle e falhas

O lifecycle registra `Created`, `DeliveryAttempted`, `Delivered`, `DeliveryFailed`,
`Deduplicated`, `Escalated`, `Read`, `Actioned` e `Expired`. Falha ou exceção do sink vira
`DeliveryFailed`, com apenas o tipo da exceção nos diagnósticos; conteúdo do provider é omitido.

Um alerta entregue ou lido que chega ao prazo sem `Actioned` vira `Expired` e conta como ignorado.
Alertas expirados não podem ser marcados retroativamente como lidos ou acionados.

## Métrica e critério de morte

`alerts metrics` calcula por política:

- `actionRate = actionedAlerts / deliveredAlerts`;
- `ignoredRate = expiredWithoutAction / deliveredAlerts`.

Antes de `minimumDeliveredAlerts`, a decisão é `InsufficientSample`. Depois disso, ação abaixo de
`minimumActionRate` ou ignorados acima de `maximumIgnoredRate` resulta em `Disable`. Caso contrário,
a decisão é `Continue`. O relatório recomenda o desligamento; ele não altera silenciosamente a
política versionada.

## Limitações

- o sink atual é local; integrações externas ainda precisam implementar a interface pluggable;
- o store local coordena concorrência no processo, portanto deve haver um avaliador Jarvis por root;
- a avaliação ocorre depois de comandos `run`/`experiment` ou por `alerts evaluate`; agendamento do
  sistema operacional fica fora do processo do Jarvis;
- o lifecycle local é evidência operacional, não uma extensão assinada do Evidence Graph.
