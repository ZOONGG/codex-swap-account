param(
    [switch]$RemoveNonSecretSettings
)

$ErrorActionPreference = "Stop"
$installRoot = Join-Path $env:LOCALAPPDATA "CodexProfileOverlay"
$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Codex Profile Overlay.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "Codex Profile Overlay.lnk"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

Get-Process CodexProfileOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($shortcut in @($startMenuShortcut, $desktopShortcut)) {
    if (Test-Path -LiteralPath $shortcut) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}
if (Test-Path -LiteralPath $runKey) {
    Remove-ItemProperty -Path $runKey -Name "CodexProfileOverlay" -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $installRoot) {
    Get-ChildItem -LiteralPath $installRoot -Force |
        Where-Object {
            $RemoveNonSecretSettings -or $_.Name -notin @('settings.json', 'profiles.json', 'active-profile.txt', 'logs', 'backups', 'removed-profiles')
        } |
        Remove-Item -Recurse -Force
}

Write-Host "Uninstalled application files. .codex, .codex-profiles, auth.json files, chats, settings, databases, and history were not removed."
