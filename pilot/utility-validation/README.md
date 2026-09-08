# Piloto de utilidade com 10 tarefas reais

Este pacote operacional atende a issue
[#100](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/100) sem substituir a coleta real
por exemplos artificiais. Ele define o protocolo, o manifesto e a validação automática que devem ser
preenchidos antes e depois da execução com provider real e revisor independente.

## Arquivos

- `pilot-utility-protocol.md`: protocolo pré-registrado, limiares e regras de interrupção.
- `pilot-utility-manifest.template.json`: manifesto a copiar e preencher antes da primeira execução.
- `pilot-utility-report.template.md`: relatório final a preencher com links/hashes de evidência.
- `Validate-PilotUtilityProtocol.ps1`: validador fail-closed do manifesto e do relatório.

## Uso

```powershell
Copy-Item pilot\utility-validation\pilot-utility-manifest.template.json `
  C:\pilotos\aecs\pilot-utility-manifest.json

# Preencha as 10 tarefas reais autorizadas e congele o arquivo antes da coleta.
pwsh pilot\utility-validation\Validate-PilotUtilityProtocol.ps1 `
  -Manifest C:\pilotos\aecs\pilot-utility-manifest.json `
  -RequireFrozen

# Depois da coleta e da revisão independente:
pwsh pilot\utility-validation\Validate-PilotUtilityProtocol.ps1 `
  -Manifest C:\pilotos\aecs\pilot-utility-manifest.json `
  -Report C:\pilotos\aecs\pilot-utility-report.md `
  -RequireFrozen `
  -RequireCompleted
```

O validador não executa o AECS e não chama providers. Ele impede que a entrega seja marcada como
aceita quando o manifesto ainda está incompleto, quando há menos de 10 tarefas distintas, quando o
provider é `mock`, quando falta revisor independente ou quando métricas obrigatórias ainda não foram
registradas.

Para validar apenas a sintaxe do template versionado no repositório, use
`-AllowTemplatePlaceholders`. Não use esse modo no aceite do piloto.
