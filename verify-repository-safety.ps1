$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path

$tracked = & git -C $repo ls-files
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed."
}

$forbiddenPaths = @(
    'auth.json',
    'active-profile.txt',
    'settings.json',
    'profiles.json',
    '.codex-profiles',
    'removed-profiles',
    'preflight-backups',
    'backups',
    'logs'
)

$authJsonShape = (('"refresh' + '_token"'), ('"access' + '_token"'), ('"id' + '_token"')) -join '|'
$patterns = @(
    @{ Name = 'authorization-header'; Regex = '(?i)authorization\s*:\s*bearer\s+[a-z0-9._~+/=-]{12,}' },
    @{ Name = 'access-token-field'; Regex = '(?i)(access|refresh|id)[_-]?token["''\s:=]+[a-z0-9._~+/=-]{12,}' },
    @{ Name = 'cookie-header'; Regex = '(?i)\bcookie\s*:\s*[^;=]+=' },
    @{ Name = 'private-key'; Regex = '-----BEGIN [A-Z ]*PRIVATE KEY-----' },
    @{ Name = 'user-specific-path'; Regex = '(?i)C:\\Users\\najde\\|/Users/najde/' },
    @{ Name = 'auth-json-shape'; Regex = "(?i)$authJsonShape" }
)

$failures = New-Object System.Collections.Generic.List[string]

foreach ($path in $tracked) {
    $normalized = $path -replace '\\', '/'
    foreach ($forbidden in $forbiddenPaths) {
        if ($normalized -like "*$forbidden*") {
            $failures.Add("forbidden-path: $normalized")
        }
    }

    $fullPath = Join-Path $repo $path
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Length -gt 2MB -or $bytes.Contains([byte]0)) {
        continue
    }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    foreach ($pattern in $patterns) {
        $count = [regex]::Matches($text, $pattern.Regex).Count
        if ($count -gt 0) {
            $failures.Add("$($pattern.Name): $normalized ($count match(es), value redacted)")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Error ("Repository safety scan failed:`n" + ($failures -join "`n"))
    exit 1
}

Write-Host "Repository safety scan passed. No tracked credential-like files or secret-like patterns were found."
