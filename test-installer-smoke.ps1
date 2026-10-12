param(
    [string]$InnoCompiler = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    [string]$PublishedPayload = "",
    [string]$PreviousPayload = "",
    [string]$TargetVersion = "1.0.1"
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$identity = 'CodexSwapAccountSmoke-' + [Guid]::NewGuid().ToString('N')
$root = Join-Path $repo "artifacts\$identity"
$payload = Join-Path $root 'payload'
$installed = Join-Path $root 'app'
$fakeHome = Join-Path $root 'fake-user'
New-Item -ItemType Directory -Force $payload, $installed, $fakeHome | Out-Null
$sentinels = @('.codex\auth.json', '.codex-profiles\fake\auth.json', 'LocalAppData\CodexProfileOverlay\settings.json')
foreach ($name in $sentinels) {
    $path = Join-Path $fakeHome $name
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    Set-Content -LiteralPath $path -Value 'FAKE TEST DATA ONLY'
}
$hashes = @{}
foreach ($name in $sentinels) { $hashes[$name] = (Get-FileHash (Join-Path $fakeHome $name)).Hash }
# Compile the production script with isolated identity, no shortcuts and no app launch.
$script = Get-Content -LiteralPath (Join-Path $repo 'installer\CodexSwapAccount.iss') -Raw
$script = $script.Replace('CodexSwapAccount', $identity).Replace("'CodexProfileOverlay'", "'$identity'")
$script = $script.Replace('Local\CodexProfileOverlay', "Local\$identity")
$script = [regex]::Replace($script, '(?ms)^\[Icons\].*?(?=^\[Code\])', '')
$icon = Join-Path $repo 'src\CodexProfileOverlay\Assets\AppIcon.ico'
$script = [regex]::Replace($script, '(?m)^SetupIconFile=.*$', "SetupIconFile=$icon")
$marker = Join-Path $root 'installation.id'
Set-Content -LiteralPath $marker -Value $identity
$script = $script.Replace('Source: "installation.id"', "Source: `"$marker`"")
$source = Join-Path $root 'smoke.iss'
Set-Content -LiteralPath $source -Value $script -Encoding utf8
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${identity}_is1"
function Run-TestInstaller([string]$file, [string[]]$arguments) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Smoke installer failed with code $($process.ExitCode)" }
}
function Assert-Sentinels {
    foreach ($name in $sentinels) {
        if ((Get-FileHash (Join-Path $fakeHome $name)).Hash -ne $hashes[$name]) { throw "Data modified: $name" }
    }
}
try {
    New-Item -Path $runPath -Force | Out-Null
    New-ItemProperty -Path $runPath -Name $identity -Value '"C:\fake-portable\CodexProfileOverlay.exe"' -PropertyType String -Force | Out-Null
    foreach ($version in @('1.0.0', $TargetVersion)) {
        $selectedPayload = if ($version -eq '1.0.0' -and $PreviousPayload) { $PreviousPayload } else { $PublishedPayload }
        if ($selectedPayload) {
            Copy-Item -Path (Join-Path $selectedPayload '*') -Destination $payload -Recurse -Force
        } else {
            Set-Content -LiteralPath (Join-Path $payload 'CodexProfileOverlay.exe') -Value "FAKE PAYLOAD $version - NEVER EXECUTE"
        }
        & $InnoCompiler "/DProductVersion=$version" "/DPublishDir=$payload" "/DArtifactsDir=$root" $source | Out-File (Join-Path $root "compile-$version.txt")
        if ($LASTEXITCODE -ne 0) { throw 'Smoke installer compile failed' }
        $setup = Join-Path $root "$identity-win-x64-setup.exe"
        Run-TestInstaller $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$installed`"", "/LOG=`"$root\install-$version.log`"")
        $metadata = Get-ItemProperty -LiteralPath $uninstallPath
        if ($metadata.DisplayVersion -ne $version) { throw 'Installer version mismatch' }
        if ($metadata.InstallLocation.TrimEnd('\') -ne $installed.TrimEnd('\')) { throw 'InstallLocation missing or incorrect' }
        $startup = (Get-ItemProperty -LiteralPath $runPath).$identity
        if ($startup -ne ('"' + (Join-Path $installed 'CodexProfileOverlay.exe') + '"')) { throw 'Startup path not migrated' }
        foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $installed $relative)).Hash) {
                throw "Installed payload mismatch: $($file.Name)"
            }
        }
        Assert-Sentinels
    }
    Run-TestInstaller (Join-Path $installed 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$root\uninstall.log`"")
    if (Test-Path -LiteralPath $uninstallPath) { throw 'Uninstall registration remains' }
    if (Test-Path -LiteralPath (Join-Path $installed 'CodexProfileOverlay.exe')) { throw 'Installed payload remains' }
    if ((Get-ItemProperty -LiteralPath $runPath -Name $identity -ErrorAction SilentlyContinue)) { throw 'Startup registration remains' }
    Assert-Sentinels
    Write-Host "PASS: isolated install, upgrade, startup migration, uninstall and fake-data preservation. Logs: $root"
}
finally {
    # Only this test's unique startup value; never remove the shared Run key.
    Remove-ItemProperty -LiteralPath $runPath -Name $identity -ErrorAction SilentlyContinue
}
