#!/usr/bin/env python3
"""Add network access and capabilities to experiment task files."""
import os
import re

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TASKS_DIR = os.path.join(REPO, "tasks", "experiment")

SANDBOX_CAPABILITIES = """    sandbox:
      network_access: true
      cpu_limit: "2.0"
      memory_limit: "1g"
      process_limit: 256
      wall_clock_seconds: 300
    capabilities:
      file_system:
        read:
          - "**"
        write:
          - "**/bin/**"
          - "**/obj/**"
          - ".aecs-verification/**"
      processes:
        - executable: git
          argument_prefix: ["--version"]
          phases: ["baseline.tool-probe"]
        - executable: dotnet
          argument_prefix: ["--version"]
          phases: ["baseline.tool-probe"]
        - executable: dotnet
          argument_prefix: ["build"]
          phases: ["baseline.build", "candidate.build"]
        - executable: dotnet
          argument_prefix: ["test"]
          phases: ["baseline.test", "candidate.test", "candidate.acceptance"]
        - executable: dotnet
          argument_prefix: ["list"]
          phases: ["baseline.security-scan", "candidate.security-scan"]
      network:
        destinations:
          - "*"
        phases:
          - baseline.tool-probe
          - baseline.build
          - candidate.build
          - baseline.test
          - candidate.test
          - candidate.acceptance
      resources:
        cpu_limit: "2.0"
        memory_limit: "1g"
        process_limit: 256
        wall_clock_seconds: 300"""

BUDGET_UPDATE = """  budget:
    tokens: 60000
    usd: 0.20
    retries: 1
    wall_clock_seconds: 600
    max_files_changed: 5"""

VERIFICATION_UPDATE = """  verification:
    build: required
    unit_tests: required
    scope: required
    integration_tests: optional
    security_scan: optional
    architecture: optional
    critical_semantic_failures: optional"""

def update_task_file(filepath):
    with open(filepath, "r") as f:
        content = f.read()
    
    # Skip if already has network_access
    if "network_access: true" in content:
        print(f"  Skipping (already configured): {os.path.basename(filepath)}")
        return
    
    # Replace execution section
    # Find "  execution:" and replace until "  budget:"
    exec_pattern = r'(  execution:\n(?:    .+\n)+?)(  budget:)'
    match = re.search(exec_pattern, content)
    if match:
        new_exec = f"""  execution:
    working_directory: sample/SampleProject
    target: SampleProject.sln
    runtime: docker
{SANDBOX_CAPABILITIES}
"""
        content = content[:match.start()] + new_exec + "\n" + match.group(2) + content[match.end():]
    
    # Update budget
    budget_pattern = r'  budget:\n(?:    .+\n)+?(?=  verification:)'
    content = re.sub(budget_pattern, BUDGET_UPDATE + "\n", content)
    
    # Update verification
    ver_pattern = r'  verification:\n(?:    .+\n)+?(?=  approval:)'
    content = re.sub(ver_pattern, VERIFICATION_UPDATE + "\n", content)
    
    with open(filepath, "w") as f:
        f.write(content)
    print(f"  Updated: {os.path.basename(filepath)}")

print("Updating experiment task files...")
for fname in sorted(os.listdir(TASKS_DIR)):
    if fname.endswith(".yaml"):
        update_task_file(os.path.join(TASKS_DIR, fname))

print("Done!")
