# Demonstração real reproduzível

Documento de controle da issue
[#99](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/99), parte da entrega
[#94](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/94).

## Objetivo

A demonstração AgronomoPlus prova a promessa central do AECS em um recorte C#/.NET representativo:
executar uma tarefa sob limites explícitos, preservar o checkout original, produzir evidência
assinada, rejeitar candidatos inelegíveis e promover somente um diff autenticado por ação separada.

Ela usa candidatos determinísticos em formato `FILE:`. Portanto, demonstra o plano de controle, não
a capacidade de um modelo real escrever a solução.

## Executar

Na raiz do AECS:

```powershell
$env:PATH = "$env:TEMP\aecs-dotnet-10-stable;$env:PATH"
.\demos\Run-RealWorldDemo.ps1
```

Para escolher o diretório de artefatos:

```powershell
.\demos\Run-RealWorldDemo.ps1 -OutputRoot C:\temp\aecs-demo
```

O script cria o diretório de saída quando necessário, define `AECS_E2E_REPORT_PATH` fora do
repositório e executa a categoria `RealWorldE2E`. Ele não instala dependências, não baixa modelos,
não chama provider pago e não promove automaticamente nada no checkout do AECS.

## Cenários

| Cenário | Decisão esperada | Evidência |
| --- | --- | --- |
| `agro-001-valid` | `Verified` | Handler e testes mudam dentro do escopo; build, testes e aceite filtrado passam. |
| `agro-002-failing-tests` | `Rejected` | O candidato muda testes dentro do escopo, mas deixa a implementação quebrada; o gate `Tests` falha. |
| `agro-003-adversarial-scope` | `Rejected` | O candidato tenta escrever em Infrastructure, fora do escopo permitido; o gate `Scope` falha. |
| promoção de `agro-001-valid` | `Promoted` | O mesmo diff autenticado é aplicado por `CandidatePromotionService` e fica staged no repositório temporário. |

## Artefatos

O relatório `report.json` contém SHA da fixture, duração, decisão, estado final, gates, arquivos
alterados, hash do diff, localização da evidência e status da promoção. Evidências JSON ficam em
subdiretório `evidence/` dentro do diretório de saída.

Os artefatos são gerados fora do checkout do AECS e não incluem segredos nem chaves privadas. O
fixture fonte em `tests/fixtures/real-world-demo` não contém `.git`, outputs de build, banco de
dados ou dependência de rede.

## Repetição e limpeza

Cada execução copia o fixture para um repositório temporário novo e inicializa uma baseline Git
local. Para repetir, execute o script novamente com outro `-OutputRoot`, ou remova o diretório de
artefatos anterior antes da nova execução.

Para limpar:

```powershell
Remove-Item -LiteralPath C:\temp\aecs-demo -Recurse -Force
```

Use somente o caminho de artefatos escolhido para a demonstração. Não remova o diretório raiz do
repositório.

## Provider real

Para medir capacidade de agente, execute um roteiro separado com Ollama ou cloud fallback
explicitamente configurado, contrato pré-registrado e custos reportados. Os resultados dessa medição
não devem ser misturados com a demonstração determinística.
