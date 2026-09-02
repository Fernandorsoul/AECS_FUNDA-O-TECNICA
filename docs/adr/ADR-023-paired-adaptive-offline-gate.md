# ADR-023: Avaliar o roteamento adaptativo em gate causal offline pareado

- Status: Accepted
- Date: 2026-09-02

## Context

O Adaptive Controller introduzido pela issue #34 opera somente em shadow mode. Ele lê evidências
autenticadas, produz uma recomendação limitada pelo plano fixo e registra a avaliação da execução
real. Por desenho, `counterfactualExecuted` permanece `false`: o sistema sabe se a recomendação
concordou com o plano fixo, mas não sabe qual teria sido o resultado caso ela fosse aplicada.

Os experimentos H1 executados até agora também não resolvem essa lacuna. Eles comparam duas
estratégias de contexto sobre as mesmas três tarefas sintéticas, enquanto a recomendação adaptativa
pode alterar modelo, estratégia de contexto e budget. Tratar os 60 runs de H1 como evidência causal
do Adaptive Controller confundiria dois tratamentos diferentes.

Há ainda duas limitações de implementação relevantes. `AdaptiveShadowPlan` não identifica o
provider; e o pipeline operacional usa o budget do `TaskContract`, não o budget presente no
`ExecutionPlan`. Aplicar diretamente a recomendação nesse caminho produziria provenance incompleta
e não exercitaria necessariamente o plano declarado.

A issue #35 exige evidência positiva antes de qualquer influência adaptativa, além de feature flag,
canary, rollback e guardrails imutáveis. O ADR-006 exige pelo menos 50 observações e progressão
controlada antes de aprendizado. Portanto, é necessário um gate causal próprio antes de decidir a
#35.

## Decision

Criar um protocolo separado, `aecs.adaptive-offline-dataset/v1`, em vez de reinterpretar datasets
H1. O protocolo será executável apenas por um comando experimental explícito e nunca será alcançado
pelo comando operacional `run`.

### Unidade experimental e corpus

- Um manifesto representa um único repositório e uma única identidade de provider.
- O repositório registra origem, revisão imutável, licença e política de redistribuição. Código de
  terceiros não é incorporado ao AECS sem autorização explícita.
- Cada tarefa registra contrato, baseline imutável, seed, evidência autenticada que contém a
  recomendação shadow e cutoff temporal do histórico.
- A decisão que alimentará a #35 exige pelo menos 50 tarefas distintas. Repetições medem variância,
  mas não contam como tarefas adicionais.
- Estudos local e cloud usam manifests, políticas de custo e relatórios separados.

### Cutoff e prevenção de leakage

Antes de executar qualquer braço, o preflight relê e autentica a evidência da recomendação e todas
as `sourceEvidenceIds`. Toda fonte precisa ser terminal, reproduzível, pertencente ao mesmo escopo de
repositório e anterior ao cutoff UTC pré-registrado. A recomendação precisa estar `Ready`, sem
diagnósticos de adulteração, e seus risco, tipo de tarefa e fingerprint do objetivo precisam
corresponder ao contrato avaliado.

Fonte ausente, posterior ao cutoff, incompatível ou não autenticada invalida o par antes de chamar o
provider. A falha permanece no relatório e não pode ser removida da amostra.

### Braços pareados

O braço de controle executa o `FixedPlan` autenticado; o candidato executa o `RecommendedPlan`
autenticado. Ambos partem do mesmo commit limpo e usam a mesma tarefa, seed, identidade de provider,
parâmetros de inferência, ambiente de verificação e oráculos.

O provider fica congelado no manifesto e é igual nos dois braços. Enquanto a recomendação shadow
não carregar identidade de provider própria, o gate não pode comparar troca de provider nem servir
como autorização para essa capacidade em produção.

O preflight exige que:

- scope, capabilities e verificações sejam idênticos nos dois planos;
- tokens, custo, duração, retries e arquivos do candidato não excedam o plano fixo;
- modelo e estratégia de contexto executados correspondam exatamente ao braço declarado;
- toda estratégia de contexto seja suportada pelo compilador vigente;
- cada braço persista evidência autenticada e preserve o checkout original.

Para exercer um budget recomendado menor sem alterar o pipeline operacional, o executor experimental
materializa uma cópia efêmera do contrato com os limites reduzidos antes de criar o pipeline do
braço. A cópia não pode ampliar nenhum limite e deve ser registrada no resultado. O comando `run`
continua usando apenas o contrato e o controlador fixo.

### Métricas e decisão

O relatório pareado registra VCC, first-pass VCC, tokens, custo efetivo e estimado, latência, retries,
falhas, violações de scope/segurança, contexto efetivo, planos e IDs/hashes de todas as evidências.
Pares incompletos continuam no denominador planejado.

A hipótese, os thresholds de ganho, as taxas máximas de falha/regressão, a confiança de 95% e a
política para referência com zero VCC são congelados antes da primeira inferência. A conclusão é
`Maintain`, `Adjust` ou `Abandon`; nenhum desses valores ativa automaticamente a #35.

`Maintain` exige simultaneamente amostra completa, ausência de regressão de guardrails, efeito
mínimo atingido e intervalo de confiança compatível com a regra pré-registrada. Dados insuficientes,
recomendação não `Ready`, custo incompatível ou incerteza resultam em `Adjust`. Violação de
guardrail, falha acima do limite ou efeito abaixo do critério de morte resultam em `Abandon`.

## Consequences

A avaliação adaptativa deixa de depender de concordância observacional e passa a exigir tratamento
executado em isolamento. Resultados H1 permanecem válidos para estratégia de contexto, mas não são
reclassificados como evidência do Adaptive Controller.

O gate adiciona custo porque executa dois braços e autentica novamente todo o histórico usado. Em
troca, torna explícitos leakage temporal, equivalência dos pares e provenance. Checkpoints e
`--resume` são necessários para que esse custo não seja repetido após interrupções.

A issue #88 implementa o contrato e o executor. A issue #89 seleciona o corpus, executa pelo menos
50 tarefas reais e publica os artefatos. A #35 permanece bloqueada até a conclusão da #89 e ainda
precisará resolver feature flag, canary, rollback e identidade de provider antes de qualquer
ativação operacional.
