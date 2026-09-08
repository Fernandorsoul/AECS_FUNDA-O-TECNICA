[CmdletBinding()]
param(
    [string]$Version = '0.1.0-pilot',

    [string]$WorkRoot = '',

    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

function Invoke-Required {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    $output = & $FileName @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($AllowedExitCodes -notcontains $exitCode) {
        $rendered = $output -join [Environment]::NewLine
        throw "$FileName $($Arguments -join ' ') failed with exit code $exitCode.$([Environment]::NewLine)$rendered"
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-Aecs {
    param(
        [string]$PublishDirectory,
        [string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    $exe = Join-Path $PublishDirectory 'AECS.Cli.exe'
    if (Test-Path -LiteralPath $exe) {
        return Invoke-Required $exe $Arguments $AllowedExitCodes
    }

    $dll = Join-Path $PublishDirectory 'AECS.Cli.dll'
    if (-not (Test-Path -LiteralPath $dll)) {
        throw "Published AECS CLI was not found in $PublishDirectory."
    }
    $dotnetArguments = @($dll) + $Arguments
    return Invoke-Required dotnet $dotnetArguments $AllowedExitCodes
}

function Convert-JsonOutput {
    param([object[]]$Output)

    $text = ($Output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    $start = $text.IndexOf('{')
    if ($start -lt 0) {
        throw "Command did not emit JSON output.$([Environment]::NewLine)$text"
    }
    $depth = 0
    $inString = $false
    $escaped = $false
    for ($index = $start; $index -lt $text.Length; $index++) {
        $character = $text[$index]
        if ($inString) {
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq '\') {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }
        if ($character -eq '"') {
            $inString = $true
            continue
        }
        if ($character -eq '{') {
            $depth++
            continue
        }
        if ($character -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $text.Substring($start, $index - $start + 1) | ConvertFrom-Json
            }
        }
    }
    throw "Command did not emit a complete JSON object.$([Environment]::NewLine)$text"
}

$repoRoot = (& git rev-parse --show-toplevel).Trim()
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    throw 'This script must run inside the AECS Git repository.'
}
Set-Location $repoRoot

$status = (& git status --porcelain=v1)
if (-not $AllowDirty -and -not [string]::IsNullOrWhiteSpace($status)) {
    throw 'Working tree must be clean to rehearse upgrade preservation. Use -AllowDirty only for local rehearsal.'
}

if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) "aecs-pilot-upgrade-preservation-$([Guid]::NewGuid().ToString('N'))"
}

$workRootFull = [IO.Path]::GetFullPath($WorkRoot)
$previousPublish = Join-Path $workRootFull "aecs-cli-$Version-previous"
$currentPublish = Join-Path $workRootFull "aecs-cli-$Version-current"
$evidenceRoot = Join-Path $workRootFull 'evidence-json'
$keyDirectory = Join-Path $workRootFull 'evidence-keys'
$runtimeConfig = Join-Path $workRootFull 'aecs.runtime.upgrade-preservation.json'

New-Item -ItemType Directory -Force -Path $workRootFull, $evidenceRoot, $keyDirectory | Out-Null

$config = [ordered]@{
    schemaVersion = 'aecs.runtime-config/v1'
    agent = [ordered]@{
        mode = 'mock'
    }
    evidence = [ordered]@{
        backend = 'json'
        jsonRoot = $evidenceRoot
        keyDirectory = $keyDirectory
    }
    execution = [ordered]@{
        allowHostExecution = $false
    }
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runtimeConfig -Encoding utf8

foreach ($publish in @($previousPublish, $currentPublish)) {
    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }
    Invoke-Required dotnet @(
        'publish',
        'src/AECS.Cli/AECS.Cli.csproj',
        '--configuration',
        'Release',
        '--output',
        $publish
    ) | Out-Null
}

$rotate = Invoke-Aecs $previousPublish @(
    'evidence-key',
    'rotate',
    '--runtime-config',
    $runtimeConfig,
    '--show-effective-config'
)
$previousConfig = Convert-JsonOutput $rotate.Output

$privateKeys = @(Get-ChildItem -LiteralPath $keyDirectory -Recurse -File -Filter 'current-private.pem')
$publicKeys = @(Get-ChildItem -LiteralPath $keyDirectory -Recurse -File -Filter '*.public.pem')
if ($privateKeys.Count -lt 1 -or $publicKeys.Count -lt 1) {
    throw 'Upgrade rehearsal did not create the expected evidence keyring files.'
}

$doctor = Invoke-Aecs $currentPublish @(
    'doctor',
    '--repo',
    $repoRoot,
    '--runtime-config',
    $runtimeConfig,
    '--mock',
    '--format',
    'json'
) -AllowedExitCodes @(0, 1)
$doctorJson = Convert-JsonOutput $doctor.Output
if ($doctorJson.schemaVersion -ne 'aecs.doctor/v1') {
    throw "Doctor output schema was not preserved across upgrade."
}

$list = Invoke-Aecs $currentPublish @(
    'evidence',
    'list',
    '--repo',
    $repoRoot,
    '--runtime-config',
    $runtimeConfig,
    '--format',
    'json'
)
$listJson = Convert-JsonOutput $list.Output
if ($null -eq $listJson.items) {
    throw 'Evidence list output is missing its items collection after upgrade.'
}

$secondRotate = Invoke-Aecs $currentPublish @(
    'evidence-key',
    'rotate',
    '--runtime-config',
    $runtimeConfig,
    '--show-effective-config'
)
$currentConfig = Convert-JsonOutput $secondRotate.Output
if ($previousConfig.configurationHash -ne $currentConfig.configurationHash) {
    throw 'Runtime configuration hash changed across package directories.'
}

$privateKeysAfter = @(Get-ChildItem -LiteralPath $keyDirectory -Recurse -File -Filter 'current-private.pem')
$publicKeysAfter = @(Get-ChildItem -LiteralPath $keyDirectory -Recurse -File -Filter '*.public.pem')
if ($privateKeysAfter.Count -lt $privateKeys.Count -or $publicKeysAfter.Count -lt $publicKeys.Count) {
    throw 'Upgrade rehearsal lost evidence keyring files.'
}

$report = [ordered]@{
    schema_version = 'aecs.pilot-upgrade-preservation/v1'
    version = $Version
    repository = $repoRoot
    runtime_config = $runtimeConfig
    evidence_root = $evidenceRoot
    key_directory = $keyDirectory
    previous_publish = $previousPublish
    current_publish = $currentPublish
    configuration_hash = $currentConfig.configurationHash
    doctor_status = $doctorJson.status
    evidence_items = $listJson.items.Count
    private_key_files_before = $privateKeys.Count
    public_key_files_before = $publicKeys.Count
    private_key_files_after = $privateKeysAfter.Count
    public_key_files_after = $publicKeysAfter.Count
}

$reportPath = Join-Path $workRootFull 'pilot-upgrade-preservation-report.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Output 'AECS pilot upgrade preservation rehearsal passed.'
Write-Output "Report: $reportPath"
Write-Output "Configuration hash: $($currentConfig.configurationHash)"
