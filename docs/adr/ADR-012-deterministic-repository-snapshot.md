# ADR-012: Inventariar a baseline com RepositorySnapshot determinístico

- **Status:** Accepted
- **Date:** 2026-08-31

## Context

`BaselineSnapshot` identificava commit, branch, status e caminho, mas não descrevia a estrutura do repositório usada para compilar contexto e executar gates. Auditoria e replay não tinham um objeto versionado para responder quais soluções, projetos, frameworks, manifests, dependências, entrypoints e suites existiam na baseline.

Ler recursivamente o filesystem seria sensível a arquivos untracked, outputs gerados, line endings, symlinks e diferenças do host. Incluir conteúdo-fonte ou segredos na evidência ampliaria desnecessariamente a superfície de exposição. Vincular o endereço de conteúdo a commit, TaskContract ou versão de ferramenta impediria reconhecer inventários iguais em ambientes distintos.

## Decision

Toda execução nova cria `aecs.repository-snapshot/v1` no worktree detached de preflight, antes de executar código do repositório. A estratégia `git-tree-manifest-inventory/v1` usa a árvore do commit Git como autoridade, IDs de blobs como hashes de arquivo e parsers limitados/fail-closed para manifests reconhecidos.

O snapshot inventaria arquivos, soluções, projetos, linguagens, frameworks, manifests, pacotes, entrypoints, suites e relações. Diretórios de VCS/IDE, dependências materializadas, outputs, artefatos, arquivos sensíveis, symlinks e submodules são excluídos. `aecs.repository-snapshot-profile/v1` permite somente exclusões adicionais relativas e seguras.

Dois fingerprints são separados:

- `configurationHash` cobre perfil e exclusões efetivas;
- `snapshotHash` cobre schema, estratégia, configuração e inventário ordenado.

Commit-base, TaskContract, ferramentas e quantidade de entradas excluídas são proveniência autenticada, não parte do endereço de conteúdo. O envelope recalcula e valida hashes e relações. O replay reconstrói o snapshot e assina hashes e diff; o Evidence Graph o projeta como nó próprio.

## Consequences

- A mesma baseline incluída e configuração produzem representação e hash idênticos entre execuções e hosts.
- Monorepos, soluções em subdiretórios e múltiplos projetos podem ser auditados sem armazenar seu conteúdo.
- Mudanças adicionadas, removidas ou alteradas ficam explícitas no replay.
- Alterações apenas em áreas excluídas não mudam o endereço do inventário, embora o novo commit permaneça registrado como proveniência.
- Novos ecosystems ou mudanças de descoberta exigem uma nova versão de estratégia/schema para preservar hashes históricos.
- Manifests reconhecidos inválidos, grandes demais ou acessados por caminho inseguro interrompem o snapshot antes do agente.
- Evidências anteriores sem o campo continuam compatíveis, mas não recebem retroativamente essa garantia.
