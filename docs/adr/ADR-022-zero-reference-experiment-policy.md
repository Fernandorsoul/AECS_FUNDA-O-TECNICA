# ADR-022: Pré-registrar a política para referência experimental sem VCC

- Status: Accepted
- Date: 2026-09-02

## Context

O A/B local corrigido do Context Compiler produziu mudanças verificadas apenas na candidata. Como a
eficiência da referência foi exatamente zero, a razão de melhora relativa ficou indefinida, embora
todos os pares tivessem custo positivo. A regra original retornava `Adjust` com uma mensagem genérica
de custo indisponível, que não descrevia a causa real.

Tratar uma candidata positiva sobre referência zero como ganho infinito seria matematicamente e
experimentalmente incorreto. Escolher uma métrica absoluta somente depois de observar esse resultado
também violaria o pré-registro.

## Decision

Preservar em datasets A/B v2/v3 a decisão conservadora: referência com zero VCC e custo válido torna
a melhora relativa indefinida e conclui `Adjust` com razão específica.

Introduzir `aecs.experiment-dataset/v4`. O protocolo v4 exige `zeroReferencePolicy` explícita:

- `Adjust` mantém a decisão conservadora e proíbe limiar absoluto;
- `AbsolutePairedDelta` exige `minimumAbsoluteImprovement` não negativo e finito.

Quando `AbsolutePairedDelta` foi pré-registrada e a referência observada tem zero VCC, usar a
distribuição pareada da diferença de VCC por custo estimado. Concluir `Maintain` somente se a média
atingir o limiar e o limite inferior do intervalo de confiança de 95% ficar estritamente acima dele.
Se a média não atingir o limiar, concluir `Abandon`; incerteza remanescente conclui `Adjust`.

Separar esse caso de custo ausente ou não positivo. Manter falhas e violações de escopo como critérios
de morte anteriores à decisão estatística. Publicar a política, sua origem pré-registrada, o caso
observado, a métrica efetiva e o limiar em `aecs.experiment-report/v5` e `analysis.csv`.

## Consequences

O resultado já observado não é reclassificado e datasets antigos não podem ganhar uma política nova
sem mudar schema, conteúdo e hash. Futuros experimentos conseguem representar o caso de referência
zero sem infinito, ambiguidade ou escolha pós-hoc.

O limiar absoluto depende da unidade da política de custo e precisa ser justificado antes da coleta.
Comparações entre datasets com políticas de custo diferentes continuam inválidas. O corpus futuro de
50 tarefas deve adotar v4 e congelar a política antes da primeira inferência.
