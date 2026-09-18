# Factorial ledger × context experiment fixture

Mock dataset for the pre-registered 2×2 factorial protocol
(`aecs.experiment-dataset/v5`, design `factorial-ledger-context-2x2`).

Arms:

| Arm | Constraint Ledger | Context strategy |
|-----|-------------------|------------------|
| A (reference) | off | naive-path-order |
| B | on | naive-path-order |
| C | off | graph-ranked |
| D | on | graph-ranked |

Protocol validation only — mock provider results do not demonstrate efficacy.
