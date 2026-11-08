param([Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$CandidateSha256)

$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') {
    throw 'Run only inside a fresh Windows Sandbox with networking disabled.'
}

$oldSetup = Join-Path $PSScriptRoot 'CodexSwapAccount-win-x64-setup.exe'
$newSetup = Join-Path $PSScriptRoot 'v1.1.1-setup.exe'
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexSwapAccount'
$exe = Join-Path $install 'CodexProfileOverlay.exe'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexSwapAccount_is1'
$data = Join-Path $env:LOCALAPPDATA 'CodexProfileOverlay'
$profiles = Join-Path $env:USERPROFILE '.codex-profiles'
$shared = Join-Path $env:USERPROFILE '.codex'
$results = Join-Path $env:TEMP 'CodexSwapAccount-upgrade-acceptance'
foreach ($path in $install, $registration, $data, $profiles, $shared, $results) {
    if (Test-Path -LiteralPath $path) { throw "Sandbox must be clean: $path" }
}

function Assert-Hash($Path, $Expected) {
    if ((Get-FileHash -LiteralPath $Path).Hash -ine $Expected) {
        throw "Unexpected SHA-256: $Path"
    }
}

function Invoke-Setup($Path, $Arguments, $LogName) {
    $log = Join-Path $results $LogName
    $process = Start-Process -FilePath $Path -ArgumentList "$Arguments /LOG=`"$log`"" -PassThru
    if (-not $process.WaitForExit(180000)) { throw "Installer timed out: $log" }
    if ($process.ExitCode -ne 0) { throw "Installer exit code $($process.ExitCode): $log" }
}

function Assert-Version($Version) {
    if ((Get-ItemProperty -LiteralPath $registration).DisplayVersion -ne $Version -or
        (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne $Version) {
        throw "Expected installed application and registration version $Version."
    }
}

function Write-Fixture($Path, $Value) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    Set-Content -LiteralPath $Path -Value $Value -Encoding Ascii
}

function Get-StartupLog {
    Get-ChildItem -LiteralPath (Join-Path $data 'logs') -Filter 'overlay-*.log' -ErrorAction SilentlyContinue |
        Sort-Object Name | Get-Content
}

function Confirm-Launch($Stage, $PreviousLog) {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $log = @(Get-StartupLog)
        $newLines = @($log | Select-Object -Skip $PreviousLog.Count)
        $running = @(Get-Process -Name CodexProfileOverlay -ErrorAction SilentlyContinue |
            Where-Object Path -eq $exe)
    } until (($running.Count -eq 1 -and $newLines -match ' INFO Overlay started\.') -or (Get-Date) -ge $deadline)
    if ($running.Count -ne 1 -or -not ($newLines -match ' INFO Overlay started\.')) {
        throw "$Stage did not start the installed application. Inspect the sandbox logs."
    }

    Write-Host "$Stage. Open Settings and the profile manager from the tray."
    Write-Host 'Verify Russian, light theme, Ctrl+Alt+G, both Fixture profiles and Fixture A active.'
    Write-Host 'Check for migration/startup warnings. Close via Exit in the tray menu.'
    if ((Read-Host 'Enter PASS only if all UI checks passed and the app has exited') -cne 'PASS' -or
        -not $running[0].HasExited -or $running[0].ExitCode -ne 0) {
        throw "$Stage UI or normal exit check failed."
    }
    $errors = @(Get-StartupLog | Select-Object -Skip $PreviousLog.Count | Where-Object { $_ -match ' ERROR ' })
    if ($errors.Count) { throw "$Stage logged errors: $($errors -join ' | ')" }
    Write-Host "$Stage passed."
}

function Save-Snapshot($Stage) {
    $snapshot = [ordered]@{}
    foreach ($path in $protectedFiles) { $snapshot[$path] = (Get-FileHash -LiteralPath $path).Hash }
    $actualSettings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    $settings = [ordered]@{}
    foreach ($name in 'language', 'theme', 'scale', 'automaticallyDownloadUpdates') {
        if ($actualSettings.$name -cne $fixtureSettings.$name) { throw "Unexpected setting: $name" }
        $settings[$name] = $actualSettings.$name
    }
    $gesture = $actualSettings.hotkeys.toggleOverlay
    if ($gesture.key -ne 71 -or $gesture.modifiers -cne 'Alt, Control') {
        throw 'Unexpected toggle hotkey.'
    }
    $settings['toggleOverlay'] = $gesture
    $snapshot['settings'] = $settings
    $snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $results "$Stage.json")
}

Assert-Hash $oldSetup '9567C1348E0CB577FD7317A4FEADCE3FB322E5A29A73B2FDDAF6DF26ADEAFA45'
Assert-Hash $newSetup $CandidateSha256
if ((Get-Item -LiteralPath $newSetup).VersionInfo.ProductVersion.Trim() -ne '1.1.1') {
    throw 'Expected candidate setup version 1.1.1.'
}
New-Item -ItemType Directory -Path $results | Out-Null
Start-Transcript -Path (Join-Path $results 'acceptance.log')
try {
    Invoke-Setup $oldSetup '/SILENT /NORESTART' 'install-1.1.0.log'
    Assert-Version '1.1.0'
    Assert-Hash $exe 'D448B0DBCC01A896F8735E358377A4598EB1B8C7B6048E3FC38440D21A7C903D'

    $authA = Join-Path $profiles 'fixture-a\auth.json'
    $authB = Join-Path $profiles 'fixture-b\auth.json'
    $sharedAuth = Join-Path $shared 'auth.json'
    $history = Join-Path $shared 'history.jsonl'
    $metadata = Join-Path $data 'profiles.json'
    $active = Join-Path $data 'active-profile.txt'
    $backup = Join-Path $data 'backups\fixture.txt'
    $settingsFile = Join-Path $data 'settings.json'
    $protectedFiles = $authA, $authB, $sharedAuth, $history, $metadata, $active, $backup

    Write-Fixture $authA '{"OPENAI_API_KEY":"fixture-a-invalid"}'
    Write-Fixture $authB '{"OPENAI_API_KEY":"fixture-b-invalid"}'
    Write-Fixture $sharedAuth '{"OPENAI_API_KEY":"fixture-a-invalid"}'
    Write-Fixture $history '{"session_id":"fixture","ts":1,"text":"synthetic shared history"}'
    Write-Fixture $metadata '{"profiles":[{"directoryName":"fixture-a","displayName":"Fixture A","order":0},{"directoryName":"fixture-b","displayName":"Fixture B","order":1}]}'
    Write-Fixture $active 'fixture-a'
    Write-Fixture $backup 'synthetic backup sentinel'
    $settingsJson = '{"language":"Russian","theme":"Light","scale":1.15,"automaticallyDownloadUpdates":false,"hotkeys":{"toggleOverlay":{"modifiers":"Alt, Control","key":71}}}'
    $fixtureSettings = $settingsJson | ConvertFrom-Json
    Write-Fixture $settingsFile $settingsJson

    Start-Process -FilePath $exe
    Confirm-Launch 'Baseline v1.1.0' @()
    Save-Snapshot 'before'

    $uninstaller = (Get-ItemProperty -LiteralPath $registration).UninstallString.Trim('"')
    $previousLog = @(Get-StartupLog)
    Invoke-Setup $newSetup '/UPDATE=1 /SILENT /NORESTART' 'upgrade-1.1.1.log'
    Assert-Version '1.1.1'
    if ((Get-ItemProperty -LiteralPath $registration).UninstallString.Trim('"') -ne $uninstaller) {
        throw 'Upgrade did not reuse the original uninstall entry.'
    }
    Confirm-Launch 'Installer relaunch v1.1.1' $previousLog
    Save-Snapshot 'after-upgrade'

    $previousLog = @(Get-StartupLog)
    Start-Process -FilePath $exe
    Confirm-Launch 'Manual relaunch v1.1.1' $previousLog
    Save-Snapshot 'after-relaunch'

    Invoke-Setup $uninstaller '/SILENT /NORESTART' 'uninstall.log'
    $deadline = (Get-Date).AddSeconds(30)
    while ((Test-Path -LiteralPath $registration) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $registration)) {
        throw 'Uninstall left the installed EXE or registration behind.'
    }
    Save-Snapshot 'after-uninstall'
    $before = Get-Content -LiteralPath (Join-Path $results 'before.json') -Raw
    foreach ($stage in 'after-upgrade', 'after-relaunch', 'after-uninstall') {
        if ((Get-Content -LiteralPath (Join-Path $results "$stage.json") -Raw) -cne $before) {
            throw "User data changed at $stage. Compare the JSON snapshots in $results."
        }
    }
    Write-Host 'PASS: official v1.1.0 -> v1.1.1, relaunch and uninstall.'
} finally {
    $appLogs = @(Get-StartupLog)
    if ($appLogs.Count) { $appLogs | Set-Content -LiteralPath (Join-Path $results 'overlay.log') }
    Stop-Transcript
    Write-Host "Evidence: $results"
}
