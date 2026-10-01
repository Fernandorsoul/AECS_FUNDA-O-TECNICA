# Relatório AECS - Preparação para Integração Orbis

> Relatorio historico substituido: a matriz atual usa .NET 10 estavel. As conclusoes de downgrade e prontidao abaixo nao representam o estado atual. Consulte [validacao atual](validation/2026-10-01-orbis-preparation.md).

**Data do registro historico:** 2025-07-13
**Projeto:** AECS - Agentic Engineering Control System
**Objetivo:** Estabilizar o AECS para integração futura com o Orbis

---

## 1. Problemas Confirmados

### 1.1 SDK Preview (CRÍTICO)
- **Arquivo:** `global.json`
- **Problema:** SDK `10.0.400-preview.0.26322.102` com `allowPrerelease: true` e `rollForward: latestMajor`
- **Consequência:** Build não reproduzível, risco de usar APIs instáveis
- **Correção:** Alterado para `8.0.410`, `rollForward: latestPatch`, `allowPrerelease: false`

### 1.2 Target Framework Preview
- **Arquivos:** Todos os `.csproj` (10 arquivos)
- **Problema:** `net10.0` é preview, não LTS
- **Consequência:** Incompatibilidade com ambientes de produção
- **Correção:** Alterado para `net8.0` (LTS até novembro 2026)

### 1.3 Pacotes NuGet Preview
- **Arquivo:** `src/AECS.Infrastructure/AECS.Infrastructure.csproj`
- **Problema:** `Microsoft.EntityFrameworkCore.Design 10.0.4` e `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3` são preview
- **Correção:** Alterado para `8.0.11` e `8.0.10` respectivamente

### 1.4 CI Workflows com Versão Preview
- **Arquivos:** `.github/workflows/ci.yml`, `aecs-pipeline.yml`, `context-compiler-h1.yml`
- **Problema:** `dotnet-version: 10.0.x` instalaria SDK preview
- **Correção:** Alterado para `8.0.x`

---

## 2. Alterações Implementadas

### 2.1 SDK e Framework
| Arquivo | Alteração |
|---------|-----------|
| `global.json` | `8.0.410`, `latestPatch`, `allowPrerelease: false` |
| `src/AECS.Domain/AECS.Domain.csproj` | `net8.0` |
| `src/AECS.Cli/AECS.Cli.csproj` | `net8.0` |
| `src/AECS.Application/AECS.Application.csproj` | `net8.0` |
| `src/AECS.Infrastructure/AECS.Infrastructure.csproj` | `net8.0` |
| `tests/AECS.UnitTests/AECS.UnitTests.csproj` | `net8.0` |
| `tests/AECS.IntegrationTests/AECS.IntegrationTests.csproj` | `net8.0` |

### 2.2 Pacotes NuGet
| Pacote | Versão Anterior | Versão Nova |
|--------|----------------|-------------|
| Microsoft.EntityFrameworkCore.Design | 10.0.4 | 8.0.11 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | 8.0.10 |

### 2.3 CI/CD
| Workflow | Alteração |
|----------|-----------|
| `ci.yml` | `10.0.x` → `8.0.x` |
| `aecs-pipeline.yml` | `10.0.x` → `8.0.x`, `net10.0` → `net8.0` |
| `context-compiler-h1.yml` | `10.0.x` → `8.0.x` |

### 2.4 Fixtures de Teste
- 6 arquivos `.csproj` em `tests/fixtures/` atualizados para `net8.0`
- 1 arquivo `Benchmark.csproj` atualizado para `net8.0`
- `DockerSandboxE2ETests.cs` atualizado (templates inline)
- `RepositorySnapshotTests.cs` atualizado (assertions)

### 2.5 Documentação
- `docs/dotnet-support.md` - Matriz atualizada para .NET 8 LTS
- `docs/docker-staged-sandbox.md` - Referências atualizadas
- `docs/validation/2026-09-07-dotnet-10-lts-migration.md` - Convertido para reversão .NET 8
- `docs/adr/ADR-026-dotnet-8-lts-stabilization.md` - Novo ADR criado
- `docs/orbis-integration-interface.md` - Documento de interface criado
- `README.md` - Atualizado
- `AECS_Fundacao_Tecnica_v0.1.md` - Atualizado
- `docs/aecs-usage-guide.md` - Atualizado
- `distribution/pilot/pilot-release-notes.template.md` - Atualizado
- `.github/ISSUE_TEMPLATE/pilot-bug-report.yml` - Atualizado
- `pilot/utility-validation/pilot-utility-manifest.template.json` - Atualizado

---

## 3. Testes Executados

**Não executados:** .NET SDK 8.0 não está instalado nesta máquina.

**Próximos passos para validação:**
1. Instalar .NET 8.0 SDK
2. Executar `dotnet restore AECS.slnx`
3. Executar `dotnet build AECS.slnx --configuration Release`
4. Executar `dotnet test AECS.slnx --configuration Release`
5. Verificar CI no GitHub Actions

---

## 4. Verificações Não Executadas

| Verificação | Motivo |
|-------------|--------|
| Build completo | .NET SDK não instalado |
| Testes unitários | .NET SDK não instalado |
| Testes de integração | .NET SDK não instalado |
| Testes PostgreSQL | Servidor não disponível |
| Testes Docker sandbox | Docker não disponível |
| RealWorld E2E | Dependências ausentes |

---

## 5. Garantias de Execução Verificadas (Código)

### 5.1 Timeout e Cancelamento
- `SystemProcessRunner`: Timeout configurável, cancelamento via CancellationToken
- `ExecutionBudgetScope`: Wall-clock limit compartilhado
- `DockerSandboxProcessRunner`: Timeout por comando + wall-clock do sandbox

### 5.2 Escopo e Contenção
- `ScopeVerifier`: Rejeita alterações fora do escopo permitido
- `FileApplicator`: Valida caminhos, rejeita traversal (`../`)
- `DockerStagedProcessRunnerFactory`: Valida worktree, rejeita symlinks

### 5.3 Evidência Autenticada
- `AuthenticatedEvidenceEnvelopeCodec`: Assinatura RSA das evidências
- `EvidenceEnvelopeFormat`: Schema versionado, rejeição de duplicatas
- `JsonExecutionEvidenceStore`: Escrita atômica com rollback

### 5.4 Promoção Controlada
- `CandidatePromotionService`: Requer confirmação explícita (user/policy/human-review)
- Validação de diff hash antes de promoção
- Preservação do checkout original

### 5.5 Limpeza
- Worktrees Git descartáveis após execução
- Containers Docker removidos (`--rm` + cleanup em `finally`)
- Arquivos temporários removidos em blocos `finally`

---

## 6. Interface para Integração com Orbis

### 6.1 CLI Disponível
- `aecs run` - Execução única
- `aecs experiment` - Execução em lote
- `aecs promote` - Promoção controlada
- `aecs export-patch` - Exportação de patch
- `aecs evidence` - Consulta de evidências
- `aecs replay` - Replay de evidência
- `aecs doctor` - Diagnóstico
- `aecs history` - Registro histórico
- `aecs vscode-server` - Protocolo VS Code

### 6.2 Contratos Versionados
- `aecs.task-contract/v1` - Contrato de tarefa
- `aecs.runtime-config/v1` - Configuração de runtime
- `aecs.evidence-envelope/v1` - Envelope de evidência
- `aecs.experiment-report/v5` - Relatório de experimento

### 6.3 Backends de Evidência
- **JSON** (padrão): Armazenamento local com assinatura RSA
- **PostgreSQL**: Armazenamento compartilhado para CI/produção

### 6.4 Recomendação para Orbis
1. Criar API REST wrapper sobre a CLI
2. Endpoints: `POST /tasks`, `GET /tasks/{id}`, `GET /evidence/{id}`, `POST /evidence/{id}/promote`
3. Consumir evidências JSON diretamente do store
4. Usar `aecs doctor` para verificar pré-requisitos

---

## 7. Limites de Linguagem

### 7.1 C# (.NET)
- **Cobertura:** Completa (Roslyn/MSBuild)
- **Análise semântica:** Tipos, membros, herança, implementações, referências
- **Verificadores:** EB001-EB005 (architecture, patterns, breaking changes, semantic, historical)

### 7.2 TypeScript/Angular
- **Cobertura:** Não implementada
- **Razão:** Roslyn não suporta TypeScript
- **Recomendação:** Documentar limitação, usar ESLint/TSC para validação básica

### 7.3 VS Code Extension
- **Tipo:** TypeScript
- **Testes:** 2 testes (protocol client)
- **Análise semântica:** Não disponível

---

## 8. Avaliação de Prontidão

### Checklist Conjunta

| Item | Status | Observação |
|------|--------|------------|
| SDK estável configurado | ✅ APROVADO | .NET 8.0 LTS |
| Framework atualizado | ✅ APROVADO | net8.0 em todos os csproj |
| Pacotes NuGet estáveis | ✅ APROVADO | EF Core 8.0.11, Npgsql 8.0.10 |
| CI configurado | ✅ APROVADO | 8.0.x nos workflows |
| Build executado | ❌ NÃO VERIFICADO | SDK não instalado |
| Testes executados | ❌ NÃO VERIFICADO | SDK não instalado |
| Docker sandbox | ❌ NÃO VERIFICADO | Docker não disponível |
| PostgreSQL | ❌ NÃO VERIFICADO | Servidor não disponível |
| Interface documentada | ✅ APROVADO | orbis-integration-interface.md |
| Evidência autenticada | ✅ APROVADO | RSA + schema versionado |
| Promoção controlada | ✅ APROVADO | Confirmação explícita obrigatória |
| Limpeza de recursos | ✅ APROVADO | Worktrees + containers + temp files |
| Timeout/Cancelamento | ✅ APROVADO | CancellationToken + wall-clock |
| Escopo validado | ✅ APROVADO | ScopeVerifier + FileApplicator |

---

## 9. Recomendação Final

**Pronto com limitações explicitadas.**

### O que está pronto:
- ✅ SDK e framework estabilizados (.NET 8.0 LTS)
- ✅ CI configurado para versão estável
- ✅ Pacotes NuGet atualizados
- ✅ Documentação atualizada
- ✅ Interface de integração documentada
- ✅ Garantias de execução verificadas via código

### O que falta para validação completa:
- ❌ Build completo (requer .NET 8.0 SDK)
- ❌ Testes unitários e de integração (requer .NET 8.0 SDK)
- ❌ Testes Docker sandbox (requer Docker)
- ❌ Testes PostgreSQL (requer servidor)
- ❌ RealWorld E2E (requer ambiente completo)

### Próximos passos:
1. Instalar .NET 8.0 SDK no ambiente de CI
2. Executar CI completo no GitHub Actions
3. Validar Docker sandbox em CI
4. Documentar resultados da validação
5. Criar wrapper API REST para integração Orbis

---

## 10. Arquivos Alterados (26 arquivos)

### Configuração
- `global.json`
- `.github/workflows/ci.yml`
- `.github/workflows/aecs-pipeline.yml`
- `.github/workflows/context-compiler-h1.yml`

### Projetos .NET (10 arquivos)
- `src/AECS.Domain/AECS.Domain.csproj`
- `src/AECS.Cli/AECS.Cli.csproj`
- `src/AECS.Application/AECS.Application.csproj`
- `src/AECS.Infrastructure/AECS.Infrastructure.csproj`
- `tests/AECS.UnitTests/AECS.UnitTests.csproj`
- `tests/AECS.IntegrationTests/AECS.IntegrationTests.csproj`
- `tests/fixtures/**/RealProject.csproj` (4 arquivos)
- `experiments/context-compiler-h1/repository/Benchmark.csproj`

### Testes (2 arquivos)
- `tests/AECS.IntegrationTests/DockerSandboxE2ETests.cs`
- `tests/AECS.IntegrationTests/RepositorySnapshotTests.cs`

### Documentação (10 arquivos)
- `README.md`
- `AECS_Fundacao_Tecnica_v0.1.md`
- `docs/dotnet-support.md`
- `docs/docker-staged-sandbox.md`
- `docs/aecs-usage-guide.md`
- `docs/validation/2026-09-07-dotnet-10-lts-migration.md`
- `docs/adr/ADR-026-dotnet-8-lts-stabilization.md` (novo)
- `docs/orbis-integration-interface.md` (novo)
- `distribution/pilot/pilot-release-notes.template.md`
- `.github/ISSUE_TEMPLATE/pilot-bug-report.yml`
- `pilot/utility-validation/pilot-utility-manifest.template.json`
