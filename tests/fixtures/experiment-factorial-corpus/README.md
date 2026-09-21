# Factorial ledger × context — 20-task integration corpus

Self-contained fixture for the pre-registered 2×2 factorial protocol
(`aecs.experiment-dataset/v5`, design `factorial-ledger-context-2x2`) on a
**real C# project with a broken baseline** (RealProject: 6 of 22 tests failing).

## Why this corpus exists

The 2-task `experiment-factorial` fixture hit a **ceiling effect** with capable
providers (24/24 VCC, all contrasts = 0). Harder, integration-level tasks with
`build: required` + `unit_tests: required` against intentionally failing tests
produce real signal: candidates must repair the baseline failures *and* complete
the task-specific change, or the required Tests gate rejects them.

## Layout

- `repository/` — RealProject (src + tests + `.slnx`), baseline broken (6 failing tests)
- `repository/tasks/` — **20 contracts** `EXP-C01`..`EXP-C20` (Customers 10, Orders 5, Billing 4, cross-module 1)
- All contracts: `runtime: host`, `target: RealProject.slnx`, `working_directory: .`,
  build + unit tests + scope required, `acceptance: []`, `wall_clock_seconds: 300`
- **Every task requires the full test suite green** — i.e. fixing the six baseline
  failures plus the task-specific change (documented limitation: tasks are not
  module-isolated at the test-gate level).

## Datasets (repetitions = 1 → 80 runs each when executed)

| File | Provider | Model | Purpose |
|---|---|---|---|
| `dataset.json` | Mock | mock | CI/loader-safe |
| `dataset.cloud.json` | Cloud | xiaomi/mimo-v2.5 | real cloud run |
| `dataset.local.json` | Local | qwen2.5-coder:3b | real Ollama run |
| `dataset.smoke.json` | Mock | mock | 2-task end-to-end smoke (8 runs) |

Expected decision for all tasks is `Verified` (what a successful repair+change
yields); the Mock agent cannot repair code, so mock runs are expected to be
`Rejected` — mock validates the protocol plumbing, not task completion.

## Reproduction

```bash
cp -R tests/fixtures/experiment-factorial-corpus <tmp>
git -C <tmp>/repository init --initial-branch main && git -C <tmp>/repository add -A \
  && git -C <tmp>/repository commit -m baseline
aecs experiment --dataset <tmp>/dataset.smoke.json --output <tmp>/out \
  --include-real-providers --allow-host-execution \
  --evidence-root <tmp>/ev --key-directory <tmp>/keys
```

Loader coverage: `ExperimentFactorialCorpusTests` (unit) parses all four
datasets and all 20 contracts in CI.
