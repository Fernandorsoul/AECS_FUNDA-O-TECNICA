[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$')]
    [string]$Version,

    [string]$OutputRoot = 'artifacts/pilot-release',

    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

function Invoke-Required {
    param([string]$FileName, [string[]]$Arguments)
    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FileName $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = (& git rev-parse --show-toplevel).Trim()
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw 'This script must run inside the AECS Git repository.'
}
Set-Location $repoRoot

$status = (& git status --porcelain=v1)
if (-not $AllowDirty -and -not [string]::IsNullOrWhiteSpace($status)) {
    throw 'Working tree must be clean to create a release package. Use -AllowDirty only for local rehearsal.'
}

$sha = (& git rev-parse HEAD).Trim()
$branch = (& git branch --show-current).Trim()
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
$publishDir = Join-Path $outputRootFull "aecs-cli-$Version"
$zipPath = Join-Path $outputRootFull "aecs-cli-$Version.zip"
$manifestPath = Join-Path $outputRootFull "aecs-pilot-release-$Version.manifest.json"
$checksumsPath = Join-Path $outputRootFull "SHA256SUMS.txt"
$redactionScript = Join-Path $repoRoot 'distribution/pilot/Test-PilotReleaseRedaction.ps1'

New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Invoke-Required dotnet @('publish', 'src/AECS.Cli/AECS.Cli.csproj', '--configuration', 'Release', '--output', $publishDir)

$inventory = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/')
        [pscustomobject]@{
            path = $relative
            bytes = $_.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }
    }

$publishedItems = Get-ChildItem -LiteralPath $publishDir -Force
Compress-Archive -LiteralPath $publishedItems.FullName -DestinationPath $zipPath -Force
$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    schema_version = 'aecs.pilot-release/v1'
    version = $Version
    source = [ordered]@{
        repository = $repoRoot
        branch = $branch
        commit = $sha
        dirty_allowed = [bool]$AllowDirty
    }
    package = [ordered]@{
        path = $zipPath
        sha256 = "sha256:$zipHash"
        published_directory = $publishDir
    }
    toolchain = [ordered]@{
        dotnet = (& dotnet --version).Trim()
        git = (& git --version).Trim()
    }
    inventory = $inventory
}

$manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $manifestPath -Encoding utf8
@(
    "$zipHash  $(Split-Path -Leaf $zipPath)"
    "$((Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant())  $(Split-Path -Leaf $manifestPath)"
) | Set-Content -LiteralPath $checksumsPath -Encoding ascii

& $redactionScript -Path @($publishDir, $zipPath, $manifestPath, $checksumsPath)

Write-Output "AECS pilot package created"
Write-Output "Version: $Version"
Write-Output "Commit: $sha"
Write-Output "Package: $zipPath"
Write-Output "SHA256: sha256:$zipHash"
Write-Output "Manifest: $manifestPath"
Write-Output "Checksums: $checksumsPath"
