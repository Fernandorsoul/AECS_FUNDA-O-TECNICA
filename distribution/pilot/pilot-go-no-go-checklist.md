# Checklist go/no-go do release piloto

## Candidato

- [ ] Versão escolhida.
- [ ] SHA candidato em `dev` congelado depois de revisão.
- [ ] CI completo verde no mesmo SHA.
- [ ] Working tree limpo usado para empacotar.

## Pacote

- [ ] `Create-PilotReleasePackage.ps1` executado a partir do SHA candidato.
- [ ] ZIP da CLI gerado.
- [ ] Manifesto `aecs.pilot-release/v1` gerado.
- [ ] `SHA256SUMS.txt` publicado.
- [ ] Inventário de arquivos e toolchain anexado.

## Instalação e primeira execução

- [ ] Instalação testada a partir do ZIP, sem binário Debug.
- [ ] `aecs doctor --repo <repo> --format json` executado.
- [ ] Quickstart validado.
- [ ] Contratos de exemplo parseiam e falham fechado quando inválidos.
- [ ] Guia `jarvis guide` validado.

## Demonstração e aceite

- [ ] Demonstração reproduzível executada com pacote final.
- [ ] Cenário aceito revisado.
- [ ] Cenário rejeitado revisado.
- [ ] Cenário de violação de escopo revisado.
- [ ] Aceite de usuário/revisor independente registrado.

## Piloto real

- [ ] Resultado do piloto de utilidade com 10 tarefas anexado.
- [ ] Provider real, tentativas e falhas mantidos no denominador.
- [ ] Métricas VCC, primeira passagem, retries, tokens, custo, latência e revisão preenchidas.
- [ ] Decisão `advance`, `fix` ou `expand_sample` registrada.

## Segurança e distribuição

- [ ] Artefatos revisados para segredos/chaves privadas.
- [ ] `Test-PilotReleaseRedaction.ps1` passou sobre ZIP, manifesto, checksums e pacote publicado.
- [ ] Conteúdo sem licença de redistribuição excluído.
- [ ] Backup/restauração de evidências e keyring documentados.
- [ ] Zero defeitos críticos/altos abertos em escopo, orçamento, integridade e promoção.
- [ ] Exceções menores têm issue, responsável e severidade.

## Feedback

- [ ] Canal de feedback definido.
- [ ] Template de bug publicado.
- [ ] Responsável pela triagem definido.
