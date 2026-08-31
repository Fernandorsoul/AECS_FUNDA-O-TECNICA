# ADR-009: Preventive Execution Capabilities

**Status:** Accepted
**Date:** 2026-08-31

## Context

O isolamento Docker reduz a superfície disponível, mas um container com workspace gravável, executáveis genéricos, rede ou credenciais ainda pode alterar, executar e exfiltrar mais do que uma tarefa precisa. Controles detectivos posteriores não desfazem esses efeitos. A política também precisa sobreviver ao planejamento adaptativo e permanecer reproduzível na evidência.

## Decision

Adotar `aecs.capabilities/v1` como política preventiva versionada e com autoridade no `TaskContract`:

- filesystem declara leitura do worktree staged e diretórios graváveis mínimos;
- processos são autorizados pela combinação de executável, prefixo de argv e fase;
- rede exige opt-in simultâneo no sandbox e na capability de fase/destino;
- segredos são variáveis de ambiente efêmeras, injetadas apenas nas fases declaradas;
- recursos da capability são tetos que o perfil do sandbox não pode ultrapassar;
- parser, preflight e runtime falham fechados para versões, regras ou concessões inválidas;
- `/workspace` é read-only, e escrita usa bind mounts específicos validados contra traversal, links e escapes;
- planos adaptativos podem reduzir, mas não expandir a política autoritativa;
- cada decisão registra versão, autoridade, hash, fase, concessões, recusas e nomes de segredos na evidência autenticada.

O runtime Docker atual só consegue impor rede `none` ou `bridge`. `"*"` é a única concessão de destino que habilita `bridge`; destinos mais estreitos falham fechados até existir um enforcer de egress apropriado. Fases que recebem segredo têm stdout e stderr integralmente suprimidos, inclusive para evitar que transformações do valor contornem redaction textual.

## Consequences

- Comandos não autorizados são recusados antes de criar um container.
- O pipeline padrão mantém somente a escrita necessária a build, testes e evidência de aceite.
- Contratos customizados precisam enumerar todos os comandos requeridos pelos gates.
- Acesso amplo de rede ou filesystem fica explícito e auditável.
- A supressão de saída reduz a capacidade de diagnóstico em fases com segredos, em troca de uma barreira conservadora contra exfiltração.
- Evidências legadas autenticadas permanecem reproduzíveis por uma política de compatibilidade marcada, sem reescrever seu payload assinado.
