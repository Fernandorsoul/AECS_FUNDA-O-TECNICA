[CmdletBinding()]
param([string] $DotnetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repositoryRoot 'experiments/context-compiler-h1/ExperimentToolchain.ps1')

function Assert-Condition($Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock] $Action, [string] $ExpectedMessage) {
    try { & $Action } catch {
        Assert-Condition ($_.Exception.Message -like "*$ExpectedMessage*") $_.Exception.Message
        return
    }
    throw "Expected failure containing '$ExpectedMessage'."
}

$savedLocation = (Get-Location).Path
$savedEnvironment = @{}
foreach ($name in @('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROLL_FORWARD')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Assert-EnvironmentRestored {
    Assert-Condition ((Get-Location).Path -eq $savedLocation) 'Caller location was changed.'
    foreach ($name in $savedEnvironment.Keys) {
        Assert-Condition ([Environment]::GetEnvironmentVariable($name, 'Process') -ceq
            $savedEnvironment[$name]) "Caller environment was changed: $name"
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('aecs-toolchain-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $fixture = Join-Path $temporaryRoot 'fixture with spaces'
    $worktree = Join-Path $temporaryRoot 'detached worktree'
    New-Item -ItemType Directory -Path $fixture | Out-Null

    # Parse the actual entrypoint, without starting its provider or experiment.
    $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repositoryRoot 'experiments/context-compiler-h1/Invoke-LocalLowHardwareH1.ps1'),
        [ref]$null, [ref]$parseErrors)
    Assert-Condition ($parseErrors.Count -eq 0) ($parseErrors -join "`n")
    $entrypoint = Get-Content (Join-Path $repositoryRoot 'experiments/context-compiler-h1/Invoke-LocalLowHardwareH1.ps1') -Raw
    Assert-Condition ($entrypoint -match '(?m)^exit \$processExitCode\r?$') 'The executor does not propagate the validated CLI exit code.'

    Invoke-AecsExperimentToolchain -DotnetPath $DotnetPath -RepositoryRoot $repositoryRoot -Action {
        param($toolchain)
        Assert-Condition ([IO.Path]::IsPathRooted($toolchain.dotnetPath)) 'dotnet path is not absolute.'
        Assert-Condition ((Get-Command dotnet -CommandType Application).Source -eq
            $toolchain.dotnetPath) 'Child commands do not resolve the selected installation.'
        Assert-Condition ($env:DOTNET_ROOT -eq $toolchain.dotnetRoot) 'Runtime root was not selected.'
        Set-AecsExperimentSdkPin -FixtureRoot $fixture -SdkVersion $toolchain.sdkVersion
        $pin = Get-Content (Join-Path $fixture 'global.json') -Raw | ConvertFrom-Json
        Assert-Condition ($pin.sdk.rollForward -eq 'disable' -and -not $pin.sdk.allowPrerelease) 'SDK pin permits drift.'

        & git -C $fixture init --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }
        & git -C $fixture add global.json
        if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
        & git -C $fixture -c user.name=AECS -c user.email=test@aecs.local -c commit.gpgsign=false commit --quiet -m baseline
        if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
        & git -C $fixture worktree add --quiet --detach $worktree HEAD
        if ($LASTEXITCODE -ne 0) { throw 'git worktree failed.' }
        Push-Location -LiteralPath $worktree
        try {
            # Resolves through PATH in a real detached worktree, as host verifiers do.
            $observed = & dotnet --version
            Assert-Condition ($LASTEXITCODE -eq 0 -and $observed -eq $toolchain.sdkVersion) 'Detached worktree SDK differs from controller.'
        } finally { Pop-Location }

        $originalPin = Get-Content (Join-Path $fixture 'global.json') -Raw
        Assert-Throws { Set-AecsExperimentSdkPin -FixtureRoot $fixture -SdkVersion '10.0.100' } 'existing fixture SDK policy'
        Assert-Condition ((Get-Content (Join-Path $fixture 'global.json') -Raw) -ceq $originalPin) 'Existing pin was overwritten.'
    }
    Assert-EnvironmentRestored
    Write-Output 'PASS: selected SDK inherited by detached worktree; existing policy preserved; environment restored.'

    Assert-Throws {
        Invoke-AecsExperimentToolchain -DotnetPath $DotnetPath -RepositoryRoot $repositoryRoot -Action {
            throw 'intentional experiment failure'
        }
    } 'intentional experiment failure'
    Assert-EnvironmentRestored
    Write-Output 'PASS: environment restored after experiment failure.'

    foreach ($version in @('9.0.100', '10.0.100-preview.1', 'invalid')) {
        Assert-Throws { Set-AecsExperimentSdkPin -FixtureRoot $temporaryRoot -SdkVersion $version } 'exact stable .NET 10'
    }
    Write-Output 'PASS: preview, other major and malformed SDK pins rejected.'

    # An unavailable repository SDK fails before the callback can execute.
    @{ sdk = @{ version = '99.0.100'; rollForward = 'disable' } } |
        ConvertTo-Json | Set-Content (Join-Path $temporaryRoot 'global.json')
    Assert-Throws {
        Invoke-AecsExperimentToolchain -DotnetPath $DotnetPath -RepositoryRoot $temporaryRoot -Action {
            throw 'Callback must not run.'
        }
    } 'cannot resolve the repository SDK policy'
    Assert-EnvironmentRestored
    Write-Output 'PASS: incompatible installation rejected before experiment.'
} finally {
    # Both repositories are owned by this test and contained in its unique temporary directory.
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith('aecs-toolchain-test-')) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}

# The negative SDK-resolution test intentionally runs dotnet with a missing SDK.
# Do not let its native exit code turn a successful assertion run into a CI failure.
exit 0
