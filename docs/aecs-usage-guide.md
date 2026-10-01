# AECS Usage Guide

## Quick Start

### Prerequisites

1. .NET 8 SDK
2. Docker Desktop
3. Python 3.12+
4. Groq API key (or Ollama for local)

### Setup

```bash
# 1. Clone repository
git clone <repo-url>
cd AECS_FUNDA-O-TECNICA

# 2. Create .env file
cat > .env << EOF
OPENAI_API_KEY=gsk_your_key_here
OPENAI_MODEL=openai/gpt-oss-20b
OPENAI_BASE_URL=https://api.groq.com/openai/v1
EOF

# 3. Generate NuGet assets (workaround for SDK preview bug)
python scripts/fix-nuget-restore.py

# 4. Build AECS CLI
dotnet build src/AECS.Cli/AECS.Cli.csproj --no-restore
```

### Running a Single Task

```bash
dotnet src/AECS.Cli/bin/Debug/net8.0/AECS.Cli.dll run \
  --repo ./sample/SampleProject \
  --task-file tasks/task-001-fix-null.yaml \
  --enable-cloud-fallback \
  --allow-cloud-context
```

### Running an Experiment (Multiple Tasks)

```bash
dotnet src/AECS.Cli/bin/Debug/net8.0/AECS.Cli.dll experiment \
  --repo ./sample/SampleProject \
  --tasks ./tasks/experiment \
  --enable-cloud-fallback \
  --allow-cloud-context
```

### Using the Dashboard

```bash
python scripts/aecs-dashboard.py
```

## Task Contract (YAML)

### Basic Structure

```yaml
schema_version: aecs.task-contract/v1
task:
  id: TASK-001
  objective: Fix NullReferenceException in CustomerMapper
  acceptance:
    - No NullReferenceException when Customer is null
    - Existing tests still pass
  acceptance_evidence:
    - id: AC-001
      type: test
      reference: "FullyQualifiedName~CustomerMapperTests"
      test_path: sample/SampleProject/tests/SampleProject.Tests/Customers/CustomerMapperTests.cs
      required: true
  scope:
    allowed:
      - sample/SampleProject/src/SampleProject/Customers/**
    forbidden:
      - sample/SampleProject/src/SampleProject/Billing/**
  constraints:
    security_risk: low
    database_migration: false
    external_dependency: false
  execution:
    working_directory: sample/SampleProject
    target: SampleProject.sln
    runtime: docker
    sandbox:
      network_access: true
      cpu_limit: "2.0"
      memory_limit: "1g"
      process_limit: 256
      wall_clock_seconds: 300
    capabilities:
      file_system:
        read: ["**"]
        write: ["**/bin/**", "**/obj/**", ".aecs-verification/**"]
      processes:
        - executable: dotnet
          argument_prefix: ["build"]
          phases: ["baseline.build", "candidate.build"]
        - executable: dotnet
          argument_prefix: ["test"]
          phases: ["baseline.test", "candidate.test", "candidate.acceptance"]
      network:
        destinations: ["*"]
        phases: ["baseline.build", "candidate.build", "baseline.test", "candidate.test", "candidate.acceptance"]
      resources:
        cpu_limit: "2.0"
        memory_limit: "1g"
        process_limit: 256
        wall_clock_seconds: 300
  budget:
    tokens: 60000
    usd: 0.20
    retries: 1
    wall_clock_seconds: 600
    max_files_changed: 5
  verification:
    build: required
    unit_tests: required
    scope: required
    critical_semantic_failures: optional
  approval:
    production: none
```

### Key Fields

| Field | Description |
|---|---|
| `objective` | What the agent should do |
| `acceptance` | Criteria for success |
| `acceptance_evidence` | Maps criteria to test files |
| `scope.allowed` | Files the agent can modify |
| `scope.forbidden` | Files the agent cannot modify |
| `execution.target` | Solution/project file to build |
| `execution.sandbox.network_access` | Enable Docker network |
| `budget.wall_clock_seconds` | Time limit for execution |

## CLI Commands

### Run

```bash
aecs run --repo <path> --task-file <path> [options]
```

### Experiment

```bash
aecs experiment --repo <path> --tasks <dir> [options]
```

### Evidence

```bash
aecs evidence list --repo <path>
aecs evidence show --repo <path> --evidence <id>
```

### Doctor

```bash
aecs doctor --repo <path>
```

## Common Options

| Option | Description |
|---|---|
| `--enable-cloud-fallback` | Use cloud model (Groq) |
| `--allow-cloud-context` | Allow sending code context to cloud |
| `--mock` | Use mock agent (no LLM) |
| `--show-effective-config` | Print resolved configuration |

## Troubleshooting

### NuGet Restore Fails

```bash
python scripts/fix-nuget-restore.py
dotnet build src/AECS.Cli/AECS.Cli.csproj --no-restore
```

### Docker Network Issues

Ensure `network_access: true` in task YAML and `*` in network destinations.

### Rate Limiting (429)

Groq free tier has rate limits. Wait between runs or upgrade to paid tier.

### Symbol Graph Errors (EB001-EB005)

MSBuildLocator has issues with .NET 10 preview SDK. Set `critical_semantic_failures: optional` in task YAML.

## Architecture

See [aecs-pipeline-architecture.md](aecs-pipeline-architecture.md) for detailed architecture documentation.

## Metrics

- **VCC**: Verified Code Changes
- **CPVC**: Cost per Verified Code Change
- **First-pass rate**: % of tasks verified on first attempt

Run `python scripts/aecs-dashboard.py` to see current metrics.
