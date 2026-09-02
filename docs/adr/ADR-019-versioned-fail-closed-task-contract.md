# ADR-019: TaskContract versionado e fail-closed

- Status: Accepted
- Date: 2026-09-02

## Context

O TaskContract é a autoridade declarativa para identidade da tarefa, escopo, orçamento,
capabilities de execução, gates de verificação, risco e aprovação humana. O parser YAML original
aceitava propriedades desconhecidas e convertia diversos valores desconhecidos para padrões
permissivos. Um erro de digitação podia, portanto, enfraquecer um gate, reduzir risco ou remover
aprovação humana sem uma decisão explícita.

O TaskContract também integra a evidência autenticada de execução. Endurecer novas entradas não
pode invalidar silenciosamente envelopes legitimamente assinados antes da existência de schema e
fingerprint do contrato.

## Decision

- Novas entradas YAML devem declarar `schema_version: aecs.task-contract/v1` na raiz do documento.
- O parser rejeita propriedades desconhecidas em qualquer nível e informa o caminho YAML completo.
- Snake case e PascalCase continuam como aliases aceitos, mas declarar as duas formas do mesmo
  campo é ambíguo e falha fechado.
- `task.id` e `task.objective` são obrigatórios, não vazios e limitados a 128 e 4.000 caracteres.
- Valores de política são enumerações fechadas. Risco, runtime, modos de verificação, aprovação,
  fases de capabilities, modos de teste e tipos de evidência de aceite desconhecidos são rejeitados.
- R0 a R4 são mapeados explicitamente. A classificação determinística ainda pode elevar o risco
  declarado antes da execução.
- O contrato efetivo é selado depois da classificação com um fingerprint SHA-256 canônico. Schema e
  fingerprint são persistidos dentro da evidência autenticada, conferidos em toda leitura e expostos
  pelo Evidence Graph.
- O runtime não aceita TaskContract YAML legado sem assinatura. Exemplos e fixtures versionados no
  repositório são migrados para v1.
- Evidência autenticada anterior a este ADR permanece legível e reproduzível. A ausência simultânea
  dos novos campos é representada como `aecs.task-contract/legacy-v0`; ela nunca recebe fingerprint
  sintético nem autoridade v1 retroativa.
- Qualquer schema de entrada futuro exige caminho explícito no parser e ADR que defina a migração.
  Versões futuras desconhecidas sempre falham fechado.

## Consequences

- Erros de digitação e produtores incompatíveis falham antes do staging do repositório.
- A evidência identifica exatamente a política efetiva usada pela execução.
- Produtores de tarefas existentes precisam emitir a versão e satisfazer a validação dos campos
  obrigatórios.
- Envelopes autenticados históricos preservam payload e assinaturas originais.
- Adicionar ou renomear um campo do contrato passa a ser uma decisão de compatibilidade versionada,
  não uma alteração silenciosa do parser.

## Alternatives rejected

- **Continuar ignorando campos desconhecidos:** permite downgrade de política por erro de grafia.
- **Atualizar YAML sem versão automaticamente:** não distingue entrada legada intencional de um
  contrato incompatível ou malformado.
- **Reescrever evidência histórica com fingerprint v1:** destrói a proveniência original e atribui
  informação que não foi autenticada no momento da execução.
