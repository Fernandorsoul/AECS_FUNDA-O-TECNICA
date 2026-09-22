# Factorial ledger × context — calibrated corpus (partial broken baseline)

Mid-difficulty fixture for the 2×2 factorial protocol, built to place VCC
**strictly inside (0,1)** — between the ceiling of the 2-task fixture
(100% with cloud) and the floor of the 20-task corpus (0/80 with cloud).

## Baseline

RealProject copy with **3 of 6 original failures remaining** (19/22 passing):

- `Customer.Equals(null)` throws NullReferenceException
- `CustomerMapper.ToDto(null)` throws NullReferenceException
- `CustomerMapper.ToEntity(null)` throws NullReferenceException

Fixed in this copy (were broken in the floor corpus): `CalculateTax` negative
rate, `CustomerService.GetActive`, `CustomerService.Deactivate`.

Every task requires the **full suite green** ⇒ the model must repair
`Customer.cs` + `CustomerMapper.cs` (2 files, Customers scope) and complete the
task-specific addition — achievable in one pass by a capable model, but not
guaranteed.

## Tasks (4)

| Id | Repair Customers failures | Plus |
|---|---|---|
| EXP-K01 | yes | — |
| EXP-K02 | yes | `Customer.ToString` (id + name) |
| EXP-K03 | yes | mapper `ToUpperInvariant` |
| EXP-K04 | yes | mapper email trim |

## Datasets (repetitions = 1 → 16 runs each)

`dataset.json` (mock) · `dataset.cloud.json` (xiaomi/mimo-v2.5) · `dataset.local.json` (qwen2.5-coder:3b)

Expected decision: `Verified`. Mock cannot repair code → mock runs expected
`Rejected` (protocol plumbing only).

Loader coverage: `ExperimentFactorialCorpusTests` (calibrated theory cases) in CI.
