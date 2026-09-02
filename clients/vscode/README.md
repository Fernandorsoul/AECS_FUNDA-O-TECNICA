# AECS para VS Code

Cliente fino para iniciar TaskContracts, acompanhar estado e cancelamento, inspecionar diff,
gates, critérios, budget e evidências, e registrar aprovação ou rejeição no backend AECS.

A extensão não interpreta elegibilidade, não verifica gates e não aplica patches. Uma aprovação
registrada aqui ainda exige o fluxo separado de promoção controlada do AECS.

## Desenvolvimento

```powershell
npm ci
npm test
npm run package
```

Configure `aecs.backendPath` com o executável publicado ou com o caminho absoluto de
`AECS.Cli.dll`. Consulte [`docs/vscode-client.md`](../../docs/vscode-client.md) para instalação,
compatibilidade, protocolo, recuperação e modelo de ameaças.
