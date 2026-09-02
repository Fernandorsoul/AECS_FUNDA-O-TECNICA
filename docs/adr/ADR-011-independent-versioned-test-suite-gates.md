# ADR-011: Independent Versioned Test Suite Gates

**Status:** Accepted
**Date:** 2026-08-31

## Context

Um único resultado `Tests` não informa se unitários, integração ou aceite foram executados. Ele também impede targets, timeouts e políticas de bloqueio diferentes e aceita falsos positivos quando `dotnet test` termina com zero casos.

## Decision

Adicionar `execution.test_suites.version: aecs.test-suites/v1` com categorias `unit`, `integration` e `acceptance`:

- cada categoria declara modo `required`, `optional` ou `disabled`, target relativo, argv adicional estruturado e timeout;
- categorias habilitadas executam na baseline e no candidato nas suas próprias fases preventivas;
- o controlador força `--no-build`, logger TRX e results directory isolado;
- `UnitTests`, `IntegrationTests` e `AcceptanceTests` produzem resultados e evidências separados;
- TRX registra descoberta, executados, aprovados, falhos e ignorados; zero testes bloqueia um gate obrigatório;
- somente gates `required` participam da decisão e do preflight bloqueante, embora opcionais também executem e sejam persistidos;
- evidência de aceite filtrada usa o perfil `acceptance` e continua sendo prova adicional do candidato;
- contratos sem o novo bloco preservam explicitamente o agregado legado `Tests`, inclusive no replay.

## Consequences

- Repositórios podem apontar unitários e integração para projetos em diretórios distintos.
- Um integration gate opcional pode revelar dívida sem impedir uma mudança cujo unit gate obrigatório passou.
- Cada comando tem teto próprio, mas nunca recebe mais que o wall clock global restante.
- Logger e results directory são reservados ao controlador para impedir evidência ambígua.
- A compatibilidade legada permanece disponível, mas novos contratos devem adotar v1 para obter separação e contagens.
