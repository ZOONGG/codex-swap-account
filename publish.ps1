param(
    [string]$Configuration = "Release",
    [string]$Output = "",
    [string]$InnoCompiler = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$localDotnet = Join-Path $repo ".dotnet\dotnet.exe"
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = [pscustomobject]@{ Source = $localDotnet }
} else {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
}

if (-not $dotnet) {
    throw "dotnet was not found. Install .NET 8 SDK or run the local SDK bootstrap used by this repository."
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repo "artifacts\publish"
}

if (Test-Path -LiteralPath $Output) {
    $resolvedOutput = [IO.Path]::GetFullPath($Output).TrimEnd('\')
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\') + '\'
    if (-not $resolvedOutput.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Existing publish output must be a subdirectory of project artifacts; refusing recursive deletion.'
    }
    Remove-Item -LiteralPath $Output -Recurse -Force
}

& $dotnet.Source publish (Join-Path $repo "src\CodexProfileOverlay\CodexProfileOverlay.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Platform=x64 `
    -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$zip = Join-Path $repo "artifacts\CodexProfileOverlay-win-x64-portable.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $zip -Force

$exe = Join-Path $Output "CodexProfileOverlay.exe"
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected product version: $version" }
if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler) { throw 'Inno Setup 6 is required to build the installer. Supply -InnoCompiler with the ISCC.exe path.' }
$artifacts = Join-Path $repo 'artifacts'
& $InnoCompiler "/DProductVersion=$version" "/DPublishDir=$([IO.Path]::GetFullPath($Output))" "/DArtifactsDir=$artifacts" (Join-Path $repo 'installer\CodexSwapAccount.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$installer = Join-Path $artifacts 'CodexSwapAccount-win-x64-setup.exe'
$checksums = Join-Path $repo "artifacts\SHA256SUMS.txt"
$checksumLines = @(
    "{0}  {1}" -f (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash, (Split-Path -Leaf $exe)
    "{0}  {1}" -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash, (Split-Path -Leaf $zip)
    "{0}  {1}" -f (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash, (Split-Path -Leaf $installer)
)
Set-Content -LiteralPath $checksums -Value $checksumLines -Encoding ascii

Write-Host "Published to $Output"
Write-Host "Portable zip: $zip"
Write-Host "Installer: $installer"
Write-Host "Checksums: $checksums"
