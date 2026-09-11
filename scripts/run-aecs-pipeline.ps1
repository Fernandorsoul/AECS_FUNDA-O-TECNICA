#!/usr/bin/env pwsh
# AECS Pipeline CI/CD Script
# Runs the AECS experiment pipeline with cloud-only mode

param(
    [string]$RepoPath = "./sample/SampleProject",
    [string]$TasksPath = "./tasks/experiment",
    [switch]$SkipBuild,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

Write-Host "=== AECS Pipeline CI/CD ===" -ForegroundColor Cyan
Write-Host "Repo: $RepoPath"
Write-Host "Tasks: $TasksPath"
Write-Host ""

# Check prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Yellow

# Check Docker
try {
    $dockerVersion = docker version --format "{{.Server.Version}}" 2>&1
    Write-Host "  Docker: $dockerVersion" -ForegroundColor Green
} catch {
    Write-Host "  Docker: NOT AVAILABLE" -ForegroundColor Red
    exit 1
}

# Check .env file
if (-not (Test-Path ".env")) {
    Write-Host "  .env file: NOT FOUND" -ForegroundColor Red
    Write-Host "  Create .env with OPENAI_API_KEY, OPENAI_MODEL, OPENAI_BASE_URL" -ForegroundColor Yellow
    exit 1
}
Write-Host "  .env file: OK" -ForegroundColor Green

# Build AECS CLI
if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "Building AECS CLI..." -ForegroundColor Yellow
    python scripts/fix-nuget-restore.py
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  NuGet restore failed" -ForegroundColor Red
        exit 1
    }
    
    dotnet build src/AECS.Cli/AECS.Cli.csproj --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Build failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "  Build: OK" -ForegroundColor Green
}

# Run experiment
Write-Host ""
Write-Host "Running AECS experiment..." -ForegroundColor Yellow
Write-Host ""

$cliPath = "src/AECS.Cli/bin/Debug/net10.0/AECS.Cli.dll"
$arguments = @(
    "experiment",
    "--repo", $RepoPath,
    "--tasks", $TasksPath,
    "--enable-cloud-fallback",
    "--allow-cloud-context"
)

if ($Verbose) {
    $arguments += "--show-effective-config"
}

dotnet $cliPath @arguments

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Experiment failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "=== Pipeline completed successfully ===" -ForegroundColor Green
