# ADR-010: Reproducible Security Scan Gate

**Status:** Accepted
**Date:** 2026-08-31

## Context

Um gate que consulta sempre uma base móvel ou registra o conteúdo detectado não é reproduzível e pode vazar o próprio segredo que deveria proteger. Ao mesmo tempo, bloquear toda dívida histórica impede adoção incremental, e confiar somente em regex não cobre dependências vulneráveis.

## Decision

Implementar `SecurityScan` como composição de `ISecurityScanner` sob a política `aecs.security-scan/v1`:

- scanners determinísticos de segredos e padrões leem o worktree sem carregar código;
- dependências são enumeradas com `dotnet list ... --format json --no-restore` por argv estruturado no sandbox;
- advisories vêm de uma snapshot local explícita e versionada;
- a baseline produz fingerprints e o candidato bloqueia somente findings novos no limiar configurado;
- supressões exigem regra, caminho seguro, justificativa e fingerprint opcional;
- findings omitem o trecho detectado e saídas de ferramenta são sanitizadas antes da evidência;
- ferramenta ausente, falha, timeout, formato inválido ou scanner ausente falha fechado;
- replay repete comandos e compara a evidência normalizada completa do scan.

## Consequences

- O mesmo contrato e snapshot produzem findings comparáveis no tempo.
- Dívida existente permanece visível sem impedir mudanças não relacionadas.
- O inventário de dependências exige target e artefatos de restore/build disponíveis no worktree.
- A cobertura de advisories só cresce por atualização explícita e revisada da snapshot.
- Regex continuam sujeitas a falso positivo, tratado por supressão estreita e auditável, não por remoção silenciosa.
