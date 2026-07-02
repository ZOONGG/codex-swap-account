param(
    [string]$Source = "",
    [switch]$StartWithWindows,
    [switch]$Launch
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path $repo "artifacts\publish"
}

if (-not (Test-Path -LiteralPath (Join-Path $Source "CodexProfileOverlay.exe"))) {
    throw "CodexProfileOverlay.exe was not found in $Source. Run .\publish.ps1 first."
}

$installRoot = Join-Path $env:LOCALAPPDATA "CodexProfileOverlay"
$shortcutDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcut = Join-Path $shortcutDir "Codex Profile Overlay.lnk"

Get-Process CodexProfileOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $Source "*") -Destination $installRoot -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $installRoot "CodexProfileOverlay.exe"
$link.WorkingDirectory = $installRoot
$link.Description = "Codex Profile Overlay"
$link.Save()

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($StartWithWindows) {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name "CodexProfileOverlay" -Value ('"{0}"' -f (Join-Path $installRoot "CodexProfileOverlay.exe"))
}

if ($Launch) {
    Start-Process -FilePath (Join-Path $installRoot "CodexProfileOverlay.exe")
}

Write-Host "Installed to $installRoot"
