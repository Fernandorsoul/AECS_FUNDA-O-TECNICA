# Fixture E2E reproduzível do AgronomoPlus

Este fixture é um recorte curado e compilável do repositório real AgronomoPlus usado nos experimentos originais do AECS. Os arquivos mantêm seus caminhos e comportamento de produção, enquanto o grafo de projetos foi reduzido aos módulos exercitados por AGRO-001 e AGRO-003.

## Proveniência e isolamento

- revisão de origem: `ecbbcac`, da branch local `desenvolvimento`;
- runtime: .NET 9, selecionado pelo `global.json` do fixture;
- `repository/`: snapshot sem `.git`, copiado para um diretório temporário e inicializado como novo repositório Git em cada cenário;
- `candidates/`: respostas determinísticas no mesmo protocolo `FILE:` usado pelos agentes;
- `expected-results.json`: oráculo versionado de decisão, estado, arquivos alterados e gates esperados.

O diretório versionado nunca é usado diretamente como alvo. O fixture não contém output de build, segredos, banco de dados nem integração dependente de rede.

## Cenários

| Cenário | Resultado esperado | O que comprova |
| --- | --- | --- |
| `agro-001-valid` | `Verified` | baseline verde, contexto do worktree, mudança de handler e teste, build/testes do candidato e dois testes de aceite filtrados |
| `agro-003-adversarial-scope` | `Rejected` | Git detecta escrita em Infrastructure apesar da alegação do agente; `Scope` bloqueia e os gates posteriores não legitimam o candidato |
| promoção de AGRO-001 | `Promoted` | uma política referenciada aplica exatamente o diff verificado e o deixa staged com auditoria persistida |

Nos cenários staged, os testes observam commit, branch, status e lista de worktrees antes e depois da execução. Nenhum worktree temporário ou artifact de teste pode vazar para o checkout original. O cenário de promoção só permite a mutação intencional posterior e exige que o diff staged seja exatamente o candidato verificado.

## Executar localmente

Na raiz do AECS:

```powershell
$env:AECS_E2E_REPORT_PATH = Join-Path $env:TEMP "aecs-real-world-e2e/report.json"
dotnet test tests/AECS.IntegrationTests/AECS.IntegrationTests.csproj `
  --filter "Category=RealWorldE2E"
```

A categoria contém dois testes xUnit: um percorre os cenários de `expected-results.json` e grava o relatório; o outro repete AGRO-001 e exerce a promoção controlada. O primeiro também persiste uma evidência JSON por cenário no diretório irmão `evidence/` do relatório.

O CI executa a mesma categoria e publica todo o diretório configurado em `AECS_E2E_REPORT_PATH` como artifact `aecs-real-world-e2e`, inclusive quando a suíte falha. Sem essa variável, o teste usa um diretório temporário e o remove depois de validar o relatório.

## Alterar o fixture

Uma mudança no snapshot, contrato ou candidato deve atualizar conscientemente `expected-results.json` e preservar estas propriedades:

1. execução offline e determinística;
2. baseline compilável antes do agente;
3. ao menos um caso verificado e um caso adversarial rejeitado;
4. caminhos de escopo e filtros de teste correspondentes ao projeto versionado;
5. ausência de `.git`, credenciais e outputs gerados no fixture fonte.
