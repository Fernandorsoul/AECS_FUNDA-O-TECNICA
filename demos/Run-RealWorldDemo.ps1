[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) ('aecs-real-world-demo-' + [Guid]::NewGuid().ToString('N'))),
    [string] $DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputRoot)
$reportPath = Join-Path $outputRoot 'report.json'

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$env:AECS_E2E_REPORT_PATH = $reportPath
& $DotnetPath test (Join-Path $repositoryRoot 'tests/AECS.IntegrationTests/AECS.IntegrationTests.csproj') `
    --configuration $Configuration `
    --filter 'Category=RealWorldE2E' `
    --logger 'console;verbosity=minimal' `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "Real-world demo failed. Report path: $reportPath"
}

Write-Output "AECS real-world demo completed."
Write-Output "Report: $reportPath"
Write-Output "Artifacts: $outputRoot"
