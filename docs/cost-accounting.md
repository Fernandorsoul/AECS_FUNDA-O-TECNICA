# Contabilidade de custo, VCC e CPVC

O schema `aecs.cost-efficiency/v1` separa telemetria de uso, estimativas monetárias e
custo reconciliado. Todos os valores monetários deste schema estão em USD. Uma estimativa de
tabela de preços ou de recurso local nunca é rotulada como cobrança do provedor.

## Definição de Verified Code Change

`aecs.vcc/v1` atribui VCC igual a 1 somente quando todas as condições abaixo são verdadeiras:

- o run terminou com status `Completed`;
- a decisão final é `Verified`;
- a mudança candidata contém ao menos um arquivo derivado pelo Git;
- o checkout original permaneceu inalterado;
- nenhum verificador de escopo falhou;
- `EvidenceId` e localização da evidência de origem foram persistidos.

Qualquer outra combinação tem VCC igual a 0 e registra uma razão explícita. First-pass VCC
adiciona a exigência de zero retries.

O conjunto de custo do CPVC inclui toda execução iniciada: mudanças verificadas, rejeições,
revisão humana e falhas. O custo de cada retry e de cada etapa de fallback faz parte do run.
`Skipped` é excluído porque o provider não foi chamado. Assim, falhas caras não desaparecem do
resultado e rejeições não melhoram artificialmente a métrica.

Para um grupo de runs:

```text
CPVC = soma(custo efetivo de todos os runs incluídos) / soma(VCC)
```

CPVC é indisponível (`null`) quando qualquer run incluído não tem custo efetivo ou quando o
grupo não produziu VCC. Custo ausente não é convertido em zero.

## Uso e custo efetivo

Cada nova chamada de adapter registra `aecs.agent-usage-accounting/v1`:

- tokens de entrada estimados antes da chamada e saída máxima reservada;
- tokens finais reportados pelo provider, quando presentes;
- divergência `final - estimado/reservado` para entrada e saída;
- request ID do provider;
- versão, hash SHA-256, data efetiva, fonte e tipo da tarifa;
- estimativa pela tabela de preços ou estimativa de recurso local;
- componentes aninhados de retries e fallback.

Ausência de telemetria final continua sendo `null`. Em uma resposta cloud bem-sucedida sem
`usage`, a estimativa conservadora do cliente pode calcular um custo de rate card, mas o campo
continua identificado como `client-estimated`. Erros de provider sem uso conhecido deixam o
custo incompleto.

A precedência do custo efetivo é:

1. custo reconciliado do ledger, identificado como `reconciled`;
2. estimativa de rate card, recurso local ou combinação das duas;
3. `null` quando nenhuma medição completa existe.

## Tabela de preços

A tabela embutida `provider-pricing.v1.json` usa schema `aecs.provider-pricing/v1`, versão
`2026-09-01`, moeda USD, data de captura, data efetiva por tarifa e URL da fonte. O hash do
conteúdo JSON normalizado acompanha cada run cloud, sem variar por quebra de linha do checkout.
A tarifa específica do modelo tem precedência sobre a
tarifa conservadora `*`; esta última é uma hipótese AECS e não uma tarifa publicada pelo
provider. Atualizar preços exige uma nova versão/data da tabela e testes da tarifa selecionada.

## Política de custo local

`aecs.local-compute-cost/v1` aplica sobre a duração medida da chamada Ollama:

```text
horas × ((potência_W / 1000 × eletricidade_USD_kWh) +
         (hardware_USD / vida_útil_horas))
```

Os defaults reproduzíveis são 200 W, USD 0,20/kWh, hardware de USD 600 e vida útil de 10.000
horas. As quatro hipóteses e a versão da política são gravadas no run. Integrações podem injetar
uma `LocalComputeCostPolicy` adequada ao equipamento; valores negativos, potência não positiva
ou vida útil não positiva falham antes da execução.

## Reconciliação

Uma exportação de cobrança pode ser associada depois dos runs, inclusive durante `--resume`:

```powershell
aecs experiment `
  --dataset .\dataset.json `
  --output C:\aecs-results\context-ab `
  --resume `
  --cost-reconciliation C:\billing\aecs-ledger.json
```

```json
{
  "schemaVersion": "aecs.cost-reconciliation/v1",
  "currency": "USD",
  "source": "provider-invoice-export-2026-09",
  "capturedAtUtc": "2026-09-30T12:00:00Z",
  "entries": [
    {
      "runKey": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "evidenceId": "11111111-1111-1111-1111-111111111111",
      "costUsd": 0.0042,
      "reference": "invoice-2026-09/request-chatcmpl-123"
    }
  ]
}
```

Run keys duplicados, propriedades JSON duplicadas, moeda/schema desconhecido, valor negativo,
referência vazia, run inexistente, run pulado ou `EvidenceId` divergente são rejeitados. O
relatório preserva o fingerprint do ledger, fonte, data, referência e o valor aplicado.

## Agregados e incerteza

O relatório v3 calcula grupos `overall`, por modelo, risco, tarefa, estratégia de contexto e dia
UTC. Cada grupo contém tamanho da amostra executada, contagens de status/VCC, cobertura e
ausência de custo, total, CPVC, distribuição do custo (quartis, média, mediana, desvio-padrão e
IC da média) e IC de 95% do CPVC por bootstrap não paramétrico determinístico com 2.000
reamostragens. `MemberRunKeys` e `EvidenceIds` ligam o agregado às linhas de `cost-records.csv`
e às evidências autenticadas.

O protocolo H1 v2 mantém sua métrica pré-registrada histórica
`vcc-per-estimated-cost`. No relatório v3, `costEfficiency` é a visão autoritativa para CPVC
reconciliado; a análise H1 continua rotulada explicitamente como estimada para não alterar o
protocolo depois da coleta.
