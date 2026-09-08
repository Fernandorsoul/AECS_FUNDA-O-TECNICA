# Recuperação de falhas e integridade - 2026-09-08

## Resultado

A issue [#101](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/101) consolida a matriz
de recuperação, integridade e ameaças residuais do piloto.

## Evidência local

Ambiente: Windows `win-x64`, SDK .NET `10.0.400`.

| Grupo | Resultado |
| --- | --- |
| Evidência, Evidence Graph, histórico e Jarvis | 33/33 passaram |
| Promoção e replay | 29/29 passaram |
| Pipeline staged, runtime e contexto real | 33/33 passaram |
| Adaptativo multi-baseline | 3/3 passaram |
| RealWorldE2E com demo empacotada | 1/1 passou |

Esses grupos cobrem cancelamento, timeout, falha de provider, baseline suja/divergente, falha de
persistência na promoção, patch inválido, evidência adulterada, keyring/chave inválida, replay
fail-closed e preservação do checkout original.

## Limites

Testes Docker completos não foram executados localmente porque o daemon Docker Desktop estava
indisponível neste host. A validação de containers deve ocorrer no CI ou em ambiente com Docker
ativo, usando a imagem staged fixada por digest.
