$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_EVENT_NAME -cnotin @('push', 'workflow_dispatch') -or
    $env:RELEASE_UPGRADE_RUNNER -cne 'github-hosted' -or -not $IsWindows) {
    throw 'Run only via the GitHub-hosted Windows workflow.'
}

$report = Join-Path $env:RUNNER_TEMP 'release-upgrade.txt'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$assetName = 'CodexSwapAccount-win-x64-setup.exe'
$candidate = Join-Path $artifacts $assetName
$payload = Join-Path $artifacts 'publish'
$oldSetup = Join-Path $env:RUNNER_TEMP "v1.1.0-$assetName"
$manifest = Join-Path $env:RUNNER_TEMP 'v1.1.0-SHA256SUMS.txt'
$profile = [Environment]::GetFolderPath('UserProfile')
$local = [Environment]::GetFolderPath('LocalApplicationData')
$install = Join-Path $local 'Programs\CodexSwapAccount'
$exe = Join-Path $install 'CodexProfileOverlay.exe'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexSwapAccount_is1'
$data = Join-Path $local 'CodexProfileOverlay'
$profiles = Join-Path $profile '.codex-profiles'
$shared = Join-Path $profile '.codex'
$wpfSmoke = [Environment]::UserInteractive -and (Get-Process -Id $PID).SessionId -ne 0

function Write-Result([string]$Message) {
    Add-Content -LiteralPath $report -Value $Message
    Write-Host $Message
}

function Get-ManifestHash([string]$Path, [string]$Name) {
    $entries = @(Select-String -LiteralPath $Path -Pattern "^([0-9a-fA-F]{64})\s+$([regex]::Escape($Name))\s*$")
    if ($entries.Count -ne 1) { throw 'Expected one checksum entry.' }
    $entries[0].Matches[0].Groups[1].Value
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) {
        throw 'SHA-256 mismatch.'
    }
}

function Invoke-Installer([string]$Path, [string]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(180000)) {
        $process.Kill($true)
        Write-Result 'Installer timed out.'
        throw 'Installer timed out.'
    }
    Write-Result "Installer exit code: $($process.ExitCode)"
    if ($process.ExitCode -ne 0) { throw 'Installer failed.' }
}

function Assert-Installation([string]$Version) {
    $entry = Get-ItemProperty -LiteralPath $registration
    if ($entry.DisplayVersion -ne $Version -or
        $entry.InstallLocation.TrimEnd('\') -ine $install -or
        (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne $Version) {
        throw 'Unexpected installation version or location.'
    }
    $uninstaller = $entry.UninstallString.Trim('"')
    if ((Split-Path -Parent $uninstaller) -ine $install -or
        (Split-Path -Leaf $uninstaller) -notmatch '^unins\d+\.exe$' -or
        -not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw 'Invalid registered uninstaller.'
    }
    $uninstaller
}

function Get-InstalledProcess {
    Get-Process -Name CodexProfileOverlay -ErrorAction SilentlyContinue | Where-Object Path -eq $exe
}

function Get-StartupCount {
    @(Get-ChildItem -LiteralPath (Join-Path $data 'logs') -Filter 'overlay-*.log' -ErrorAction SilentlyContinue |
        Select-String -Pattern ' INFO Overlay started\.$').Count
}

function Stop-InstalledProcess([Diagnostics.Process]$Process) {
    Stop-Process -InputObject $Process
    if (-not $Process.WaitForExit(10000)) { throw 'Tray process did not stop.' }
}

function Confirm-Startup([int]$PreviousCount) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $running = @(Get-InstalledProcess)
        if ($running.Count -eq 1 -and (Get-StartupCount) -gt $PreviousCount) {
            # The runner checks startup only. Stop the tray process before the next installer.
            Stop-InstalledProcess $running[0]
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The installed WPF process did not report startup.'
}

function Save-FixtureSnapshot([string]$Stage) {
    foreach ($label in $fixtures.Keys) {
        $fixture = $fixtures[$label]
        $hash = (Get-FileHash -LiteralPath $fixture.Path -Algorithm SHA256).Hash
        Write-Result "$Stage $label SHA-256: $hash"
        if ($hash -cne $fixture.Hash -or [IO.File]::ReadAllText($fixture.Path) -cne $fixture.Content) {
            throw 'Synthetic user data changed.'
        }
    }
}

$stage = 'Clean runner account'
try {
    foreach ($path in $install, $registration, $data, $profiles, $shared) {
        if (Test-Path -LiteralPath $path) { throw 'Expected a clean runner account.' }
    }
    # Keep the hosted runner user and its native HKCU/known folders for both installers.
    $env:CODEX_HOME = $shared
    Write-Result 'PASS: clean hosted runner account.'
    Write-Result 'Interactive WPF controls, tray exit and visual checks: NOT TESTED.'
    if (-not $wpfSmoke) { Write-Result 'WPF startup and relaunch: NOT TESTED (no interactive session).' }

    $stage = 'Verify official v1.1.0 assets'
    $release = 'https://github.com/ZOONGG/codex-swap-account/releases/download/v1.1.0'
    Invoke-WebRequest -Uri "$release/SHA256SUMS.txt" -OutFile $manifest -TimeoutSec 120
    Invoke-WebRequest -Uri "$release/$assetName" -OutFile $oldSetup -TimeoutSec 120
    $oldHash = Get-ManifestHash $manifest $assetName
    Assert-Hash $oldSetup $oldHash
    Assert-Hash $oldSetup '9567C1348E0CB577FD7317A4FEADCE3FB322E5A29A73B2FDDAF6DF26ADEAFA45'
    Write-Result "Official v1.1.0 setup SHA-256: $oldHash"

    $stage = 'Verify runner-built v1.1.1 candidate'
    $candidateHash = Get-ManifestHash (Join-Path $artifacts 'SHA256SUMS.txt') $assetName
    Assert-Hash $candidate $candidateHash
    $build = Get-Content -LiteralPath (Join-Path $payload 'build-info.json') -Raw | ConvertFrom-Json
    if ($build.commit -cne $env:GITHUB_SHA -or -not $build.workingTreeClean -or
        (Get-Item -LiteralPath $candidate).VersionInfo.ProductVersion.Trim() -ne '1.1.1') {
        throw 'Unexpected candidate source or version.'
    }
    Write-Result "Runner-built v1.1.1 setup SHA-256: $candidateHash"

    $stage = 'Install and verify v1.1.0'
    $silent = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    Invoke-Installer $oldSetup $silent
    $uninstaller = Assert-Installation '1.1.0'
    Assert-Hash $exe (Get-ManifestHash $manifest 'CodexProfileOverlay.exe')
    Write-Result "PASS: $stage."

    $stage = 'Seed and record synthetic data'
    $fixtures = [ordered]@{
        'Profile A' = @{ Path = Join-Path $profiles 'fixture-a\auth.json'; Content = '{"OPENAI_API_KEY":"fixture-a-invalid"}' }
        'Profile B' = @{ Path = Join-Path $profiles 'fixture-b\auth.json'; Content = '{"OPENAI_API_KEY":"fixture-b-invalid"}' }
        'Shared profile' = @{ Path = Join-Path $shared 'auth.json'; Content = '{"OPENAI_API_KEY":"fixture-a-invalid"}' }
        'History' = @{ Path = Join-Path $shared 'history.jsonl'; Content = '{"session_id":"fixture","ts":1,"text":"synthetic shared history"}' }
        'Metadata' = @{ Path = Join-Path $data 'profiles.json'; Content = '{"profiles":[{"directoryName":"fixture-a","displayName":"Fixture A","order":0},{"directoryName":"fixture-b","displayName":"Fixture B","order":1}]}' }
        'Active profile' = @{ Path = Join-Path $data 'active-profile.txt'; Content = 'fixture-a' }
        'Backup sentinel' = @{ Path = Join-Path $data 'backups\fixture.txt'; Content = 'synthetic backup sentinel' }
        'Settings' = @{ Path = Join-Path $data 'settings.json'; Content = '{"language":"Russian","theme":"Light","scale":1.15,"automaticallyDownloadUpdates":false,"launchCodexWhenOverlayStarts":false,"showAutomaticLimitIndicators":false,"hotkeys":{"toggleOverlay":{"modifiers":"Alt, Control","key":71}}}' }
    }
    foreach ($fixture in $fixtures.Values) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $fixture.Path) -Force | Out-Null
        [IO.File]::WriteAllText($fixture.Path, $fixture.Content)
        $fixture.Hash = (Get-FileHash -LiteralPath $fixture.Path -Algorithm SHA256).Hash
    }
    Save-FixtureSnapshot 'Before upgrade'
    if ($wpfSmoke) {
        $stage = 'Baseline WPF startup'
        Start-Process -FilePath $exe
        Confirm-Startup 0
        Save-FixtureSnapshot 'After baseline startup'
        Write-Result "PASS: $stage."
    }

    $stage = 'Upgrade and verify v1.1.1'
    $previousCount = Get-StartupCount
    Invoke-Installer $candidate "$silent /UPDATE=1"
    if ((Assert-Installation '1.1.1') -ine $uninstaller) { throw 'Upgrade changed the uninstall entry.' }
    $installedFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
        $installedPath = Join-Path $install ([IO.Path]::GetRelativePath($payload, $_.FullName))
        Assert-Hash $installedPath (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $installedPath
    })
    $marker = Join-Path $install 'installation.id'
    Assert-Hash $marker (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'installation.id') -Algorithm SHA256).Hash
    Save-FixtureSnapshot 'After upgrade'
    Write-Result "PASS: $stage."

    if ($wpfSmoke) {
        $stage = 'Installer-triggered WPF startup'
        Confirm-Startup $previousCount
        Save-FixtureSnapshot 'After installer launch'
        Write-Result "PASS: $stage."

        $stage = 'Explicit WPF relaunch'
        $previousCount = Get-StartupCount
        Start-Process -FilePath $exe
        Confirm-Startup $previousCount
        Save-FixtureSnapshot 'After relaunch'
        Write-Result "PASS: $stage."
    } else {
        # The stock updater requests a launch even in a noninteractive session.
        foreach ($process in Get-InstalledProcess) { Stop-InstalledProcess $process }
    }

    $stage = 'Uninstall and verify retained data'
    Invoke-Installer $uninstaller $silent
    foreach ($path in $installedFiles + @($exe, $marker, $registration)) {
        if (Test-Path -LiteralPath $path) { throw 'Uninstall left a payload file or registration behind.' }
    }
    Save-FixtureSnapshot 'After uninstall'
    Write-Result "PASS: $stage."
    Write-Result 'PASS: official v1.1.0 to runner-built v1.1.1 upgrade and uninstall.'
} catch {
    # Only fixed stage names and numeric codes leave the runner. No raw exception or application logs.
    Write-Result "FAIL: $stage. Exception HRESULT: $($_.Exception.HResult)"
    throw "Release upgrade acceptance failed during '$stage'. See the sanitized report."
} finally {
    Get-InstalledProcess | Stop-Process -ErrorAction SilentlyContinue
}
