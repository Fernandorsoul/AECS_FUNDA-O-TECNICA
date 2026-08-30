# AgronomoPlus reproducible fixture

This fixture is a curated, buildable snapshot of the real AgronomoPlus repository used by the
original AECS experiments. The source files retain their production paths and behavior while the
project graph is reduced to the modules exercised by AGRO-001 and AGRO-003.

- Source revision: `ecbbcac` from the local `desenvolvimento` branch.
- Runtime: .NET 9, selected by the fixture `global.json`.
- `repository/` is copied to a temporary directory and initialized as a fresh Git repository for
  every scenario. The checked-in fixture is never used as the execution target.
- `candidates/` contains deterministic agent responses for a valid candidate and an adversarial
  out-of-scope candidate.
- `expected-results.json` is the machine-readable scenario oracle.

The fixture intentionally contains no nested `.git` directory, build output, secrets, database,
or network-dependent integration.
