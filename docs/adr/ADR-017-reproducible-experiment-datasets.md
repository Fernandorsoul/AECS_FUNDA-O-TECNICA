# ADR-017: Experimentos como datasets versionados e runs retomáveis

- Status: Accepted
- Date: 2026-09-01

## Context

O harness recebia um diretório de contratos, executava cada tarefa uma vez com o mesmo pipeline e descartava a estrutura do experimento ao terminar. Não havia identidade de dataset/run, baseline declarada, repetição, isolamento demonstrável, retomada, comparação pareada ou exportação de observações.

## Decision

Definir `aecs.experiment-dataset/v1` com repositório, baseline, tarefas/resultado esperado, variantes, parâmetros e repetições. Resolver a referência de baseline para um commit exato e incorporá-lo ao hash do dataset.

Derivar um run key determinístico para cada tarefa, variante e repetição. Executar cada run pelo pipeline staged, que cria worktrees descartáveis, e recusar mudança da baseline ou do repositório original. Persistir checkpoint individual imutável e atômico; retomada só reutiliza um checkpoint cujo dataset/run key coincida.

Comparar cada variante com a referência na mesma tarefa/repetição, preservando falhas e skips. Exportar relatório versionado em JSON e tabelas normalizadas em CSV, sempre com vínculos para as evidências de origem.

## Consequences

Interrupções deixam trabalho retomável sem cobrar ou executar o mesmo run novamente. Resultados de modelos/estratégias passam a ser auditáveis e pareados. Outputs precisam ficar fora do repositório e configurações desconhecidas falham fechado. Providers reais permanecem opt-in e separados do smoke determinístico de CI.
