param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

function Resolve-Dotnet() {
    $dotnetCandidates = @()
    $dotnetCmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($dotnetCmd) {
        $dotnetCandidates += $dotnetCmd.Source
    }
    if ($env:DOTNET_ROOT) {
        $dotnetCandidates += (Join-Path $env:DOTNET_ROOT "dotnet.exe")
    }
    $dotnetCandidates += (Join-Path $env:USERPROFILE "dotnet\dotnet.exe")
    $dotnetCandidates = @($dotnetCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)

    if ($dotnetCandidates.Count -eq 0) {
        throw "dotnet was not found. Install .NET SDK 8.0+ from https://dotnet.microsoft.com/download."
    }

    return $dotnetCandidates[0]
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime"
$installerOutDir = Join-Path $repoRoot "artifacts\installer"
$issPath = Join-Path $PSScriptRoot "Laplace.iss"
$dotnet = Resolve-Dotnet

Write-Host "==> Publishing Laplace CLI and desktop UI..."
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

& $dotnet publish (Join-Path $repoRoot "src\Laplace.Cli\Laplace.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedValue `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

& $dotnet publish (Join-Path $repoRoot "src\Laplace.Desktop\Laplace.Desktop.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedValue `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

Write-Host "==> Compiling installer with Inno Setup..."
$isccCandidates = @()
$isccCmd = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
if ($isccCmd) {
    $isccCandidates += $isccCmd.Source
}
$isccCandidates += $env:ISCC_PATH
$isccCandidates += "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe"
$isccCandidates += "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
$isccCandidates += "D:\Inno Setup 6\ISCC.exe"
$isccCandidates = @($isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)

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
