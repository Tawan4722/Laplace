param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime"
$installerOutDir = Join-Path $repoRoot "artifacts\installer"
$issPath = Join-Path $PSScriptRoot "Laplace.iss"

Write-Host "==> Publishing Laplace CLI..."
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

dotnet publish (Join-Path $repoRoot "src\Laplace.Cli\Laplace.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedValue `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

Write-Host "==> Compiling installer with Inno Setup..."
$isccCandidates = @(
    $env:ISCC_PATH,
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $isccCandidates) {
    throw "ISCC.exe not found. Install Inno Setup 6 or set ISCC_PATH environment variable."
}

$iscc = $isccCandidates[0]
if (-not (Test-Path $installerOutDir)) {
    New-Item -ItemType Directory -Path $installerOutDir | Out-Null
}

& $iscc `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDir" `
    $issPath

Write-Host "==> Installer built successfully."
Write-Host "Output directory: $installerOutDir"
