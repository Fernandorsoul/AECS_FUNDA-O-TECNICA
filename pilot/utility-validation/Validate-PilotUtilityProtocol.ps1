[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Manifest,

    [string]$Report,

    [switch]$RequireFrozen,

    [switch]$RequireCompleted,

    [switch]$AllowTemplatePlaceholders
)

$ErrorActionPreference = 'Stop'

function Add-Failure {
    param([System.Collections.Generic.List[string]]$Failures, [string]$Message)
    $Failures.Add($Message) | Out-Null
}

function Test-Blank {
    param($Value)
    return $null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value) -or [string]$Value -eq 'preencher'
}

function Test-Sha {
    param($Value)
    return -not (Test-Blank $Value) -and [string]$Value -match '^sha256:[0-9a-f]{64}$'
}

function Test-Commit {
    param($Value)
    return -not (Test-Blank $Value) -and [string]$Value -match '^[0-9a-f]{40}$'
}

$failures = [System.Collections.Generic.List[string]]::new()
$manifestPath = [IO.Path]::GetFullPath($Manifest)
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Manifest not found: $manifestPath"
}

$manifestObject = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -Depth 64

if ($manifestObject.schema_version -ne 'aecs.pilot-utility/v1') {
    Add-Failure $failures 'schema_version must be aecs.pilot-utility/v1.'
}

if (Test-Blank $manifestObject.pilot.id) { Add-Failure $failures 'pilot.id is required.' }
if (Test-Blank $manifestObject.pilot.owner) { Add-Failure $failures 'pilot.owner is required.' }
if ($RequireFrozen -and (Test-Blank $manifestObject.pilot.frozen_at)) {
    Add-Failure $failures 'pilot.frozen_at is required when -RequireFrozen is used.'
}
if ($RequireFrozen -and -not $AllowTemplatePlaceholders -and -not (Test-Sha $manifestObject.pilot.registration_sha256)) {
    Add-Failure $failures 'pilot.registration_sha256 must be sha256:<64 hex> when -RequireFrozen is used.'
}

$runtime = $manifestObject.runtime
if ($runtime.provider_mode -notin @('local', 'cloud')) {
    Add-Failure $failures 'runtime.provider_mode must be local or cloud; mock runs do not count for utility.'
}
foreach ($field in @('provider_name', 'model', 'sdk', 'docker_image', 'hardware')) {
    if (-not $AllowTemplatePlaceholders -and (Test-Blank $runtime.$field)) { Add-Failure $failures "runtime.$field is required." }
}
if ([int]$runtime.context_window_tokens -lt 1024) {
    Add-Failure $failures 'runtime.context_window_tokens must be at least 1024.'
}
if ([int]$runtime.timeout_seconds -le 0) {
    Add-Failure $failures 'runtime.timeout_seconds must be positive.'
}
if ([int]$runtime.minimum_memory_mb -lt 1024) {
    Add-Failure $failures 'runtime.minimum_memory_mb must be at least 1024.'
}
if ([decimal]$runtime.max_cost_usd_per_task -le 0) {
    Add-Failure $failures 'runtime.max_cost_usd_per_task must be positive.'
}
if ([decimal]$runtime.max_cost_usd_total -le 0) {
    Add-Failure $failures 'runtime.max_cost_usd_total must be positive.'
}
if (@($runtime.interruption_conditions).Count -eq 0) {
    Add-Failure $failures 'runtime.interruption_conditions must define at least one stop condition.'
}

$thresholds = $manifestObject.acceptance_thresholds
if ([decimal]$thresholds.minimum_vcc_rate -le 0 -or [decimal]$thresholds.minimum_vcc_rate -gt 1) {
    Add-Failure $failures 'acceptance_thresholds.minimum_vcc_rate must be within (0, 1].'
}
if ([int]$thresholds.required_independent_reviewers -lt 1) {
    Add-Failure $failures 'acceptance_thresholds.required_independent_reviewers must be at least 1.'
}
if ([int]$thresholds.required_promoted_changes -lt 1) {
    Add-Failure $failures 'acceptance_thresholds.required_promoted_changes must be at least 1.'
}

$reviewers = @($manifestObject.independent_reviewers)
$independentReviewers = @($reviewers | Where-Object { $_.confirmed_independent -eq $true -and -not (Test-Blank $_.id) })
if ($independentReviewers.Count -lt [int]$thresholds.required_independent_reviewers) {
    Add-Failure $failures 'independent_reviewers must include the required number of confirmed independent reviewers.'
}

$tasks = @($manifestObject.tasks)
if (-not $AllowTemplatePlaceholders -and $tasks.Count -lt 10) {
    Add-Failure $failures 'tasks must contain at least 10 planned real tasks.'
}
$taskIds = @{}
$categories = @{}
foreach ($task in $tasks) {
    if (Test-Blank $task.id) {
        Add-Failure $failures 'tasks[].id is required.'
    } elseif ($taskIds.ContainsKey([string]$task.id)) {
        Add-Failure $failures "tasks id '$($task.id)' is duplicated."
    } else {
        $taskIds[[string]$task.id] = $true
    }
    if ($task.planned -ne $true) {
        Add-Failure $failures "task '$($task.id)' must remain planned in the denominator."
    }
    foreach ($field in @('name', 'url', 'authorization', 'license')) {
        if (-not $AllowTemplatePlaceholders -and (Test-Blank $task.repository.$field)) {
            Add-Failure $failures "task '$($task.id)' repository.$field is required."
        }
    }
    if (-not $AllowTemplatePlaceholders -and -not (Test-Commit $task.repository.baseline_commit)) {
        Add-Failure $failures "task '$($task.id)' repository.baseline_commit must be a 40-char hex commit."
    }
    if (-not $AllowTemplatePlaceholders -and (Test-Blank $task.task_contract)) { Add-Failure $failures "task '$($task.id)' task_contract is required." }
    if ($task.category -notin @('bugfix', 'validation', 'business-rule')) {
        Add-Failure $failures "task '$($task.id)' category must be bugfix, validation or business-rule."
    } else {
        $categories[[string]$task.category] = $true
    }
    if ($task.risk -notin @('R0', 'R1', 'R2', 'R3', 'R4')) {
        Add-Failure $failures "task '$($task.id)' risk must be R0-R4."
    }
    if (@($task.executable_acceptance).Count -eq 0) {
        Add-Failure $failures "task '$($task.id)' executable_acceptance must not be empty."
    }
    if ($task.solution_disclosed_to_agent -ne $false) {
        Add-Failure $failures "task '$($task.id)' solution_disclosed_to_agent must be false."
    }

    if ($RequireCompleted) {
        $result = $task.result
        foreach ($field in @('status', 'decision', 'vcc', 'first_pass', 'retries', 'input_tokens',
                'output_tokens', 'estimated_cost_usd', 'latency_seconds', 'review_minutes',
                'evidence_id', 'evidence_hash', 'diff_hash', 'reviewer_id', 'review_notes')) {
            if ($null -eq $result.$field -or (Test-Blank $result.$field)) {
                Add-Failure $failures "task '$($task.id)' result.$field is required when -RequireCompleted is used."
            }
        }
        if (-not (Test-Sha $result.evidence_hash)) {
            Add-Failure $failures "task '$($task.id)' result.evidence_hash must be sha256:<64 hex>."
        }
        if (-not (Test-Sha $result.diff_hash)) {
            Add-Failure $failures "task '$($task.id)' result.diff_hash must be sha256:<64 hex>."
        }
    }
}

foreach ($requiredCategory in @('bugfix', 'validation', 'business-rule')) {
    if (-not $AllowTemplatePlaceholders -and -not $categories.ContainsKey($requiredCategory)) {
        Add-Failure $failures "tasks must include category '$requiredCategory'."
    }
}

if ($RequireCompleted) {
    if ($manifestObject.pilot.decision -notin @('advance', 'fix', 'expand_sample')) {
        Add-Failure $failures 'pilot.decision must be advance, fix or expand_sample when -RequireCompleted is used.'
    }
    if (Test-Blank $Report) {
        Add-Failure $failures 'Report path is required when -RequireCompleted is used.'
    } else {
        $reportPath = [IO.Path]::GetFullPath($Report)
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            Add-Failure $failures "Report not found: $reportPath"
        } else {
            $reportText = Get-Content -Raw -LiteralPath $reportPath
            foreach ($marker in @('## Resultado agregado', '## Decisão', '## Evidências publicáveis')) {
                if (-not $reportText.Contains($marker)) {
                    Add-Failure $failures "Report must contain '$marker'."
                }
            }
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Error ("Pilot utility protocol validation failed:`n - " + ($failures -join "`n - "))
    exit 1
}

if ($AllowTemplatePlaceholders) {
    Write-Output "Pilot utility template validation passed; placeholders are allowed only for template checks."
} else {
    Write-Output "Pilot utility protocol validation passed: $($tasks.Count) tasks, $($independentReviewers.Count) independent reviewer(s)."
}
