param(
    [string]$Configuration = "Release",
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $localDotnet = Join-Path $repo ".dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $localDotnet) {
        $dotnet = [pscustomobject]@{ Source = $localDotnet }
    }
}

if (-not $dotnet) {
    throw "dotnet was not found. Install .NET 8 SDK or run the local SDK bootstrap used by this repository."
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repo "artifacts\publish"
}

if (Test-Path -LiteralPath $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}

& $dotnet.Source publish (Join-Path $repo "src\CodexProfileOverlay\CodexProfileOverlay.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Platform=x64 `
    -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$zip = Join-Path $repo "artifacts\CodexProfileOverlay-win-x64-portable.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $zip -Force

Write-Host "Published to $Output"
Write-Host "Portable zip: $zip"
