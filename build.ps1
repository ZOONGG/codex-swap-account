param(
    [string]$Configuration = "Release"
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

& $dotnet.Source build (Join-Path $repo "CodexProfileOverlay.sln") -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet.Source test (Join-Path $repo "CodexProfileOverlay.sln") -c $Configuration -p:Platform=x64 --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
