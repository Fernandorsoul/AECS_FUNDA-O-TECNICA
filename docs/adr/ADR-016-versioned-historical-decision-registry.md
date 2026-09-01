# ADR-016: Alimentar EB005 por um registro histórico revisado

- Status: Accepted
- Date: 2026-08-31

## Context

EB005 recebia uma lista em memória, mas o pipeline o instanciava com duas decisões padrão e o verificador procurava strings e `using` por regex no checkout inteiro. Não havia ingestão, versionamento, validade, autoridade, aprovação, provenance nem reprodução da regra efetivamente usada.

## Decision

Persistir decisões históricas e supressões versionadas no store operacional JSON ou PostgreSQL. Ingestão cria draft; revisão cria uma nova versão imutável. Enforcement bloqueante aprovado exige autoridade humana.

Selecionar somente a última revisão aplicável de cada decisão e somente quando seu escopo intersecta símbolos impactados no grafo candidato. Avaliar padrões como seletores de nós e relações Roslyn, nunca como busca textual. Ausência, contradição e falha de consulta são estados explícitos.

Anexar ao `VerificationResult` a seleção, os hashes, as supressões e todo conflito com origem e símbolo. O replay reutiliza essa seleção autenticada em vez de consultar regras que podem ter evoluído.

## Consequences

EB005 passa a ter uma cadeia auditável da fonte até o finding e uma regra heurística não pode bloquear sem revisão humana. Decisões exigem manutenção operacional e seletores semânticos mais precisos que palavras soltas. Histórico ausente não inventa política; histórico indisponível falha quando o gate é requerido.
