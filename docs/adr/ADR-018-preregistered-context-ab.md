# ADR-018: Pré-registrar o A/B do Context Compiler

- Status: Accepted
- Date: 2026-09-01

## Context

O harness v1 repetia variantes e preservava evidências, mas o nome `contextStrategy` era apenas
metadado: todas as variantes usavam o mesmo compilador orientado ao grafo. Também não havia
hipótese, tamanho de amostra, critério de morte ou regra estatística definidos antes dos runs.
Uma comparação assim permitiria confounds e conclusões escolhidas depois de observar os dados.

## Decision

Introduzir `aecs.experiment-dataset/v2` para o desenho `paired-context-ab`. A referência usa
`naive-path-order`, sem objetivo, símbolos ou grafo; a candidata usa `graph-ranked`. O contrato
exige duas variantes com provider, modelo, seed, parâmetros e limites idênticos, além de H1,
métrica primária, amostra mínima, confiança e critérios de morte explícitos.

Registrar VCC, first-pass sem retry, tokens, custo estimado, latência, falhas, violações de
escopo e retries como rework operacional. Agregar distribuições e intervalos de confiança sem
remover pares incompletos. Concluir `Maintain`, `Adjust` ou `Abandon` por regra determinística.
Custo zero ou ausente torna a métrica econômica indisponível, nunca infinita.

## Consequences

O A/B mede uma diferença real de contexto e rejeita configurações contaminadas antes de chamar
um provider. O benchmark de 30 pares é versionado, mas providers reais continuam opt-in e fora
do CI barato. A análise H1 permanece, por pré-registro, em custo estimado do adapter. O relatório
v3 posterior adiciona, sem reescrever H1, reconciliação de cobrança e custo de compute local
conforme [Contabilidade de custo, VCC e CPVC](../cost-accounting.md). Rework significa retry
dentro do run e não deve ser apresentado como rework pós-merge.
