# Release piloto AECS

Este diretório prepara a issue
[#102](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/102). Ele não publica release
automaticamente; o objetivo é tornar o SHA candidato, pacote, checksums, inventário e aceite
revisáveis por alguém que não acompanhou o desenvolvimento.

## Arquivos

- `Create-PilotReleasePackage.ps1`: gera pacote versionado da CLI a partir do `HEAD` atual.
- `pilot-release-notes.template.md`: notas de release, limitações, suporte, rollback e feedback.
- `pilot-go-no-go-checklist.md`: checklist de aceite operacional antes de publicar.
- `../../docs/pilot-feedback-triage.md`: canal de feedback, template de bug, redaction e
  responsável por triagem.

## Fluxo

1. Faça merge dos PRs da entrega piloto e rode CI completo em `dev`.
2. Escolha a versão piloto e o SHA candidato.
3. Em checkout limpo desse SHA, execute:

   ```powershell
   pwsh distribution\pilot\Create-PilotReleasePackage.ps1 `
     -Version 0.1.0-pilot `
     -OutputRoot artifacts\pilot-release
   ```

4. Teste instalação e primeira execução usando apenas o ZIP produzido.
5. Preencha as notas e o checklist com checksums, CI, evidências, aceite independente e resultado do
   piloto de utilidade.
6. Registre defeitos e achados usando o template `AECS pilot bug report` no GitHub; não publique
   segredos, chaves privadas, `.env`, connection strings ou dumps com memória.

Não use binários Debug, diretórios locais do desenvolvedor ou evidências sem hash no pacote final.
