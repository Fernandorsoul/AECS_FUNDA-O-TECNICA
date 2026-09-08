[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'

$SensitiveNamePatterns = @(
    '(^|[\\/])\.env($|[\\.\\/])',
    '(^|[\\/])id_rsa($|[\\.\\/])',
    '(^|[\\/])id_dsa($|[\\.\\/])',
    '(^|[\\/])id_ecdsa($|[\\.\\/])',
    '(^|[\\/])id_ed25519($|[\\.\\/])',
    '\.(pem|pfx|p12|key)$',
    'private[-_]?key',
    'connection[-_]?string',
    'secret[-_]?key'
)

$SensitiveContentPatterns = @(
    '-----BEGIN (RSA |DSA |EC |OPENSSH |)PRIVATE KEY-----',
    '(?i)\b(openai|anthropic|github|gh|api)[-_ ]?(api[-_ ]?)?key\b\s*[:=]\s*["'']?[A-Za-z0-9_\-]{20,}',
    '(?i)\b(connectionstring|connection_string|connection string)\b\s*[:=]\s*["'']?[^"'']{12,}',
    '(?i)\b(password|passwd|pwd)\b\s*[:=]\s*["'']?[^"'']{8,}',
    'Host=[^;\r\n]+;[^;\r\n]*(Username|User ID|Password)=',
    'AccountKey=[A-Za-z0-9+/=]{40,}',
    'gh[pousr]_[A-Za-z0-9_]{20,}',
    'sk-[A-Za-z0-9]{20,}'
)

$TextExtensions = @(
    '.cmd', '.config', '.cs', '.csproj', '.json', '.md', '.props', '.ps1', '.sh',
    '.sln', '.slnx', '.targets', '.txt', '.xml', '.yaml', '.yml'
)

function Get-RedactionFinding {
    param(
        [string]$DisplayPath,
        [string]$PhysicalPath
    )

    $normalized = $DisplayPath.Replace('\', '/')
    foreach ($pattern in $SensitiveNamePatterns) {
        if ($normalized -match $pattern) {
            [pscustomobject]@{
                path = $DisplayPath
                kind = 'file-name'
                pattern = $pattern
            }
        }
    }

    if (-not (Test-Path -LiteralPath $PhysicalPath -PathType Leaf)) {
        return
    }
    $extension = [IO.Path]::GetExtension($PhysicalPath).ToLowerInvariant()
    if ($TextExtensions -notcontains $extension) {
        return
    }
    $content = Get-Content -LiteralPath $PhysicalPath -Raw -ErrorAction Stop
    foreach ($pattern in $SensitiveContentPatterns) {
        if ($content -match $pattern) {
            [pscustomobject]@{
                path = $DisplayPath
                kind = 'content'
                pattern = $pattern
            }
        }
    }
}

function Test-Directory {
    param(
        [string]$Root,
        [string]$Prefix
    )

    Get-ChildItem -LiteralPath $Root -Recurse -Force -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        $display = if ([string]::IsNullOrWhiteSpace($Prefix)) {
            $relative
        }
        else {
            "$Prefix/$relative"
        }
        Get-RedactionFinding -DisplayPath $display -PhysicalPath $_.FullName
    }
}

$findings = New-Object System.Collections.Generic.List[object]
$tempRoots = New-Object System.Collections.Generic.List[string]

try {
    foreach ($inputPath in $Path) {
        $fullPath = [IO.Path]::GetFullPath($inputPath)
        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "Redaction path was not found: $fullPath"
        }

        if (Test-Path -LiteralPath $fullPath -PathType Container) {
            Test-Directory -Root $fullPath -Prefix $fullPath | ForEach-Object {
                $findings.Add($_)
            }
            continue
        }

        Get-RedactionFinding -DisplayPath $fullPath -PhysicalPath $fullPath | ForEach-Object {
            $findings.Add($_)
        }

        if ([IO.Path]::GetExtension($fullPath).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) {
            $extractRoot = Join-Path ([IO.Path]::GetTempPath()) "aecs-redaction-scan-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $extractRoot | Out-Null
            $tempRoots.Add($extractRoot)
            Expand-Archive -LiteralPath $fullPath -DestinationPath $extractRoot -Force
            Test-Directory -Root $extractRoot -Prefix "$fullPath!" | ForEach-Object {
                $findings.Add($_)
            }
        }
    }
}
finally {
    foreach ($tempRoot in $tempRoots) {
        $resolved = [IO.Path]::GetFullPath($tempRoot)
        $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved).StartsWith('aecs-redaction-scan-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | ConvertTo-Json -Depth 4 | Write-Output
    throw "Pilot release redaction scan failed: $($findings.Count) sensitive artifact finding(s)."
}

Write-Output "Pilot release redaction scan passed."
