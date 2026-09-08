function Invoke-AecsExperimentToolchain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $DotnetPath,
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][scriptblock] $Action
    )

    # Resolve before changing location: relative portable SDK paths belong to the caller.
    $executable = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop |
        Select-Object -First 1).Source
    $root = Split-Path -Parent $executable
    $savedEnvironment = @{}
    foreach ($name in @('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROLL_FORWARD')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    Push-Location -LiteralPath $RepositoryRoot
    try {
        $env:PATH = $root + [IO.Path]::PathSeparator + $env:PATH
        $env:DOTNET_ROOT = $root
        $env:DOTNET_ROOT_X64 = $root
        $env:DOTNET_ROLL_FORWARD = 'LatestPatch'
        $versionOutput = & $executable --version 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw 'The selected dotnet cannot resolve the repository SDK policy.'
        }
        $sdkVersion = ($versionOutput -join "`n").Trim()
        if ($sdkVersion -notmatch '^10\.0\.\d+$') {
            throw "A stable .NET 10 SDK is required; resolved '$sdkVersion'."
        }
        $runtimes = @(& $executable --list-runtimes)
        if ($LASTEXITCODE -ne 0 -or
            -not ($runtimes -match '^Microsoft\.NETCore\.App 10\.0\.\d+ \[')) {
            throw 'The selected dotnet installation must contain a stable .NET 10 runtime.'
        }
        & $Action ([pscustomobject]@{
            dotnetPath = $executable
            dotnetRoot = $root
            sdkVersion = $sdkVersion
            runtimes = $runtimes
        })
    } finally {
        foreach ($name in $savedEnvironment.Keys) {
            if ($null -eq $savedEnvironment[$name]) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            } else {
                [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
            }
        }
        Pop-Location
    }
}

function Set-AecsExperimentSdkPin {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $FixtureRoot,
        [Parameter(Mandatory)][string] $SdkVersion
    )

    if ($SdkVersion -notmatch '^10\.0\.\d+$') {
        throw 'The fixture requires an exact stable .NET 10 SDK version.'
    }
    $pinPath = Join-Path $FixtureRoot 'global.json'
    if (Test-Path -LiteralPath $pinPath) {
        throw "Refusing to replace an existing fixture SDK policy: $pinPath"
    }
    # Commit this file with the fixture so detached verification worktrees inherit it.
    @{ sdk = @{ version = $SdkVersion; rollForward = 'disable'; allowPrerelease = $false } } |
        ConvertTo-Json | Set-Content -LiteralPath $pinPath -Encoding utf8
}
