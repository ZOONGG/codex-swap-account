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

$sourceExe = Join-Path $Source "CodexProfileOverlay.exe"
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "CodexProfileOverlay.exe was not found in $Source. Run .\publish.ps1 first."
}
$Source = Split-Path -Parent (Resolve-Path -LiteralPath $sourceExe).Path

$installRoot = Join-Path $env:LOCALAPPDATA "CodexProfileOverlay"
$shortcutDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$startMenuShortcut = Join-Path $shortcutDir "Codex Profile Overlay.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "Codex Profile Overlay.lnk"

Get-Process CodexProfileOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Copy-Item -Path (Join-Path $Source "*") -Destination $installRoot -Recurse -Force

function New-CodexProfileOverlayShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.WorkingDirectory = Split-Path -Parent $Target
    $link.Description = "Codex Profile Overlay"
    $link.IconLocation = "$Target,0"
    $link.Save()
}

$installedExe = Join-Path $installRoot "CodexProfileOverlay.exe"
New-CodexProfileOverlayShortcut -Path $startMenuShortcut -Target $installedExe
New-CodexProfileOverlayShortcut -Path $desktopShortcut -Target $installedExe

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($StartWithWindows) {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name "CodexProfileOverlay" -Value ('"{0}"' -f $installedExe)
}

if ($Launch) {
    Start-Process -FilePath $installedExe
}

Write-Host "Installed to $installRoot"
