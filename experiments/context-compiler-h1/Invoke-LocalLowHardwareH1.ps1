[CmdletBinding()]
param(
    [ValidateSet("pilot", "full")]
    [string] $Mode = "pilot",

    [string] $DatasetFile,

    [Parameter(Mandatory)]
    [string] $ArtifactRoot,

    [string] $DotnetPath = "dotnet",

    [ValidateRange(2, 60)]
    [int] $SampleIntervalSeconds = 5
)

$ErrorActionPreference = "Stop"

$artifactPath = [IO.Path]::GetFullPath($ArtifactRoot)
if (Test-Path -LiteralPath $artifactPath) {
    throw "Artifact root already exists: $artifactPath"
}

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory "../.."))
$cliProject = Join-Path $repositoryRoot "src/AECS.Cli/AECS.Cli.csproj"
$experimentRoot = Join-Path $artifactPath "experiment"
$outputRoot = Join-Path $artifactPath "output"
$evidenceRoot = Join-Path $artifactPath "evidence"
$keyRoot = Join-Path $artifactPath "keys"
$telemetryPath = Join-Path $artifactPath "host-telemetry.json"
$stdoutPath = Join-Path $artifactPath "experiment.stdout.log"
$stderrPath = Join-Path $artifactPath "experiment.stderr.log"
$datasetName = if ([string]::IsNullOrWhiteSpace($DatasetFile)) {
    if ($Mode -eq "full") {
        "dataset.local-low-hardware.json"
    } else {
        "dataset.local-low-hardware-pilot.json"
    }
} else {
    if ([IO.Path]::GetFileName($DatasetFile) -ne $DatasetFile -or
        [IO.Path]::GetExtension($DatasetFile) -ne ".json") {
        throw "DatasetFile must be a JSON filename in the experiment directory."
    }
    $DatasetFile
}
$sourceDatasetPath = Join-Path $scriptDirectory $datasetName
if (-not (Test-Path -LiteralPath $sourceDatasetPath -PathType Leaf)) {
    throw "Dataset was not found: $sourceDatasetPath"
}
$manifest = Get-Content -LiteralPath $sourceDatasetPath -Raw | ConvertFrom-Json
$modelNames = @($manifest.variants.model | Sort-Object -Unique)
$contextWindows = @($manifest.variants.parameters.contextWindowTokens | Sort-Object -Unique)
if ($modelNames.Count -ne 1 -or $contextWindows.Count -ne 1) {
    throw "Local hardware runs require one shared model and context window."
}
$modelName = $modelNames[0]
$expectedContextWindow = [int]$contextWindows[0]
$expectedRuns = [int]$manifest.repetitions *
    @($manifest.tasks).Count *
    @($manifest.variants).Count

& $DotnetPath build $cliProject --configuration Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Could not build the Release experiment runner."
}

New-Item -ItemType Directory -Path $artifactPath | Out-Null
Copy-Item -LiteralPath $scriptDirectory -Destination $experimentRoot -Recurse
New-Item -ItemType Directory -Path $outputRoot, $evidenceRoot, $keyRoot | Out-Null

git -C (Join-Path $experimentRoot "repository") init --quiet
git -C (Join-Path $experimentRoot "repository") config user.email "experiment@aecs.local"
git -C (Join-Path $experimentRoot "repository") config user.name "AECS Experiment"
git -C (Join-Path $experimentRoot "repository") add .
git -C (Join-Path $experimentRoot "repository") commit --quiet -m "baseline"
if ($LASTEXITCODE -ne 0) {
    throw "Could not initialize the isolated experiment repository."
}

$datasetPath = Join-Path $experimentRoot $datasetName
$arguments = @(
    "run",
    "--configuration", "Release",
    "--no-build",
    "--project", $cliProject,
    "--",
    "experiment",
    "--dataset", $datasetPath,
    "--output", $outputRoot,
    "--include-real-providers",
    "--allow-host-execution",
    "--evidence-store", "json",
    "--evidence-root", $evidenceRoot,
    "--key-directory", $keyRoot
)

$quotedArguments = $arguments | ForEach-Object {
    if ($_ -match '[\s"]') {
        '"' + $_.Replace('"', '\"') + '"'
    } else {
        $_
    }
}

$model = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -Method Get |
    Select-Object -ExpandProperty models |
    Where-Object { $_.name -eq $modelName } |
    Select-Object -First 1
if ($null -eq $model) {
    throw "The preregistered $modelName model is not installed in Ollama."
}

$hostInfo = Get-CimInstance Win32_ComputerSystem
$operatingSystem = Get-CimInstance Win32_OperatingSystem
$processor = Get-CimInstance Win32_Processor | Select-Object -First 1
$displayAdapters = @(Get-CimInstance Win32_VideoController | ForEach-Object {
    [ordered]@{
        name = $_.Name
        adapterRamBytes = [long]$_.AdapterRAM
        driverVersion = $_.DriverVersion
    }
})
$samples = [Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::UtcNow
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $DotnetPath `
    -ArgumentList ($quotedArguments -join " ") `
    -WorkingDirectory $repositoryRoot `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -WindowStyle Hidden `
    -PassThru
$null = $process.Handle

while (-not $process.HasExited) {
    $currentOs = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor |
        Measure-Object -Property LoadPercentage -Average |
        Select-Object -ExpandProperty Average
    $gpuUtilization = $null
    $gpuDedicatedBytes = $null
    try {
        $gpuCounters = Get-Counter -Counter @(
            '\GPU Engine(*)\Utilization Percentage',
            '\GPU Process Memory(*)\Dedicated Usage'
        ) -ErrorAction Stop
        $utilizationValues = @($gpuCounters.CounterSamples |
            Where-Object { $_.Path -like '*utilization percentage' } |
            ForEach-Object CookedValue)
        $memoryValues = @($gpuCounters.CounterSamples |
            Where-Object { $_.Path -like '*dedicated usage' } |
            ForEach-Object CookedValue)
        if ($utilizationValues.Count -gt 0) {
            $gpuUtilization = ($utilizationValues | Measure-Object -Maximum).Maximum
        }
        if ($memoryValues.Count -gt 0) {
            $gpuDedicatedBytes = ($memoryValues | Measure-Object -Maximum).Maximum
        }
    } catch {
        # GPU performance counters are optional observability, not an execution gate.
    }

    $samples.Add([ordered]@{
        timestampUtc = [DateTimeOffset]::UtcNow.ToString("O")
        elapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        cpuLoadPercentage = [double]$cpu
        freePhysicalMemoryBytes = [long]$currentOs.FreePhysicalMemory * 1KB
        gpuEngineMaximumPercentage = $gpuUtilization
        gpuProcessDedicatedMaximumBytes = $gpuDedicatedBytes
    })
    Write-Host ("{0}: {1:N0}s, {2} telemetry samples" -f $Mode, $stopwatch.Elapsed.TotalSeconds, $samples.Count)
    Start-Sleep -Seconds $SampleIntervalSeconds
    $process.Refresh()
}

$process.WaitForExit()
$process.Refresh()
$processExitCode = [int]$process.ExitCode
$stopwatch.Stop()
$finishedAt = [DateTimeOffset]::UtcNow
$reportPath = Join-Path $outputRoot "report.json"
$report = if (Test-Path -LiteralPath $reportPath) {
    Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
} else {
    $null
}
$ollamaProcesses = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/ps" -Method Get
$loadedModel = $ollamaProcesses.models |
    Where-Object { $_.name -eq $modelName } |
    Select-Object -First 1

$telemetry = [ordered]@{
    schemaVersion = "aecs.local-hardware-telemetry/v1"
    mode = $Mode
    datasetId = $manifest.id
    sourceRevision = (git -C $repositoryRoot rev-parse HEAD)
    datasetPath = $datasetPath
    startedAtUtc = $startedAt.ToString("O")
    finishedAtUtc = $finishedAt.ToString("O")
    wallClockSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
    processExitCode = $processExitCode
    host = [ordered]@{
        operatingSystem = $operatingSystem.Caption
        operatingSystemVersion = $operatingSystem.Version
        processor = $processor.Name.Trim()
        logicalProcessors = [int]$hostInfo.NumberOfLogicalProcessors
        physicalMemoryBytes = [long]$hostInfo.TotalPhysicalMemory
        displayAdapters = $displayAdapters
    }
    ollama = [ordered]@{
        model = $model.name
        digest = $model.digest
        sizeBytes = [long]$model.size
        details = $model.details
        observedContextLength = if ($null -eq $loadedModel) { $null } else { [int]$loadedModel.context_length }
        observedResidentBytes = if ($null -eq $loadedModel) { $null } else { [long]$loadedModel.size }
        observedVramBytes = if ($null -eq $loadedModel) { $null } else { [long]$loadedModel.size_vram }
        observedGpuPercentage = if ($null -eq $loadedModel -or [long]$loadedModel.size -eq 0) {
            $null
        } else {
            [Math]::Round(100 * [double]$loadedModel.size_vram / [double]$loadedModel.size, 1)
        }
    }
    sampleIntervalSeconds = $SampleIntervalSeconds
    samples = $samples
}
$telemetry | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $telemetryPath -Encoding utf8

if ($null -eq $report) {
    throw "The experiment did not produce report.json. See $stdoutPath and $stderrPath."
}
if ($report.totalTasks -ne $expectedRuns -or $report.results.Count -ne $expectedRuns) {
    throw "Expected $expectedRuns runs, got $($report.results.Count)."
}
if ($report.completedCount + $report.failedCount + $report.skippedCount -ne $expectedRuns) {
    throw "The report has an incomplete terminal run count."
}
$checkpointCount = @(Get-ChildItem -LiteralPath (Join-Path $outputRoot "runs") -Filter "*.json" -File).Count
$evidenceCount = @(Get-ChildItem -LiteralPath $evidenceRoot -Filter "*.json" -File).Count
if ($checkpointCount -ne $expectedRuns -or $evidenceCount -ne $expectedRuns) {
    throw "Expected $expectedRuns persisted runs and evidence files; got $checkpointCount and $evidenceCount."
}
if ($null -eq $loadedModel -or $loadedModel.context_length -ne $expectedContextWindow) {
    throw "Ollama did not report the preregistered $expectedContextWindow-token context window."
}
if (($report.succeeded -and $processExitCode -ne 0) -or (-not $report.succeeded -and $processExitCode -ne 1)) {
    throw "CLI exit code $processExitCode does not match experiment success=$($report.succeeded)."
}

[pscustomobject]@{
    artifactRoot = $artifactPath
    datasetId = $report.datasetId
    model = $modelName
    report = $reportPath
    telemetry = $telemetryPath
    runs = $report.results.Count
    completed = $report.completedCount
    failed = $report.failedCount
    skipped = $report.skippedCount
    verified = $report.verifiedCount
    rejected = $report.rejectedCount
    conclusion = $report.analysis.conclusion
    cliExitCode = $processExitCode
    ollamaContextLength = $loadedModel.context_length
} | Format-List
