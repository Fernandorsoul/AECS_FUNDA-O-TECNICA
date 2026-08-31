# Integridade e autenticidade das evidências

Os backends JSON e PostgreSQL persistem cada execução em um envelope `aecs.execution-evidence/v1`. O payload base é serializado de forma canônica, recebe SHA-256 e é assinado com RSA-PSS/SHA-256. `promote`, `export-patch` e `replay` validam schema, hash, assinatura e isolamento do store antes de usar qualquer campo da evidência.

Quando presente, `CSharpSymbolGraph` também é validado estruturalmente antes da leitura: versões, vínculo ao `RepositorySnapshot` e à baseline, limites, caminhos relativos, unicidade, hashes de projetos/nós/arestas e referências entre elementos precisam ser consistentes. O store recalcula `graphHash`; o `ContextManifest` só pode declarar o mesmo hash autenticado. Evidências legadas sem esse campo permanecem compatíveis, mas não ganham autoridade semântica retroativa.

## Envelope e cadeia de eventos

O documento separa três elementos:

- `evidence`: registro imutável da execução staged, sempre com a lista interna de promoções vazia;
- `promotionEvents`: exportações e promoções assinadas individualmente, com sequência e assinatura anterior;
- `replayEvents`: resultados de reprodução assinados, ligados à execução e ao candidato originais;
- `chainSeal`: cabeça assinada que cobre a quantidade de eventos e a última assinatura.

Promoções e replays usam uma única sequência global. Intercalar os dois tipos não cria cadeias paralelas: cada novo evento aponta para a assinatura do último evento de qualquer tipo.

A serialização canônica ordena propriedades JSON por nome ordinal e preserva a ordem dos arrays. Propriedades duplicadas, campos desconhecidos, comentários, trailing commas e schemas não reconhecidos são recusados. Alterar em conjunto diff, `DiffHash`, decisão ou aprovação não restaura a validade: o hash e a assinatura cobrem o payload completo.

Quando presente, `repositorySnapshot` também é validado estruturalmente antes do uso: schema e estratégia precisam ser as versões suportadas, os vínculos com baseline e TaskContract devem coincidir, caminhos/hashes não podem ser inseguros ou duplicados e `configurationHash`/`snapshotHash` são recalculados. O envelope assina ainda a proveniência de Git/.NET, o commit e o contrato associados. Evidências autenticadas anteriores ao campo continuam legíveis; elas não ganham retrospectivamente uma prova de snapshot.

O `chainSeal` detecta remoção simples do último evento. PostgreSQL persiste os eventos em linhas relacionadas e transacionais, melhorando concorrência, backup e recuperação. Uma restauração coordenada de banco e keyring para um estado antigo ainda é um rollback criptograficamente válido; detectar esse ataque exige uma âncora monotônica independente.

## Chaves

Por padrão:

- evidências: `%LOCALAPPDATA%/AECS/evidence`;
- chaves: `%LOCALAPPDATA%/AECS/keys`;
- chave ativa: `current-private.pem`;
- chaves públicas confiáveis: `<key-id>.public.pem`.

Em outros sistemas operacionais, `LocalApplicationData` define a raiz equivalente. `AECS_EVIDENCE_PATH` sobrescreve o diretório JSON e `AECS_EVIDENCE_KEY_DIRECTORY` define o keyring usado pelos dois backends. Evidências JSON e chaves precisam ficar fora do repositório-alvo. A chave privada nunca entra no envelope, no banco nem no repositório; em Unix, o arquivo criado pelo AECS recebe permissão somente para o usuário.

O `keyId` é o SHA-256 da chave pública no formato SubjectPublicKeyInfo. Uma chave ausente, desconhecida, fraca, inválida ou uma assinatura incompatível interrompe a operação de forma fechada.

### Rotação

Pare processos escritores antes da rotação e execute:

```powershell
dotnet run --project src/AECS.Cli/AECS.Cli.csproj -- evidence-key rotate
```

Para um keyring fora do diretório padrão:

```powershell
dotnet run --project src/AECS.Cli/AECS.Cli.csproj -- evidence-key rotate `
  --key-directory C:\aecs-secrets\evidence-keys
```

A rotação substitui atomicamente apenas `current-private.pem` e preserva as chaves públicas anteriores. Assim, evidências antigas continuam verificáveis e novos eventos usam o novo `keyId`. Não remova uma chave pública enquanto houver evidência assinada por ela. Backup e ACLs do keyring são responsabilidade operacional; em produção, o diretório deve ser protegido separadamente do store.

## Evidência legada

JSONs sem envelope e assinatura são recusados. Não existe fallback silencioso nem migração que declare dados legados como autênticos. Uma futura importação deverá tratá-los como material não confiável, criar um registro explícito de provenance e nunca torná-los elegíveis para promoção automaticamente.

## Limites da garantia

A assinatura prova que o documento foi produzido por uma chave confiável e não foi modificado desde então. Ela não prova que o código está correto, não compensa um contrato incompleto e não protege contra um invasor que obtenha a chave privada. O keyring local também não substitui HSM, KMS, transparência externa ou armazenamento imutável. Para operação e recuperação do banco, consulte [Store PostgreSQL de evidências](postgresql-evidence-store.md).
