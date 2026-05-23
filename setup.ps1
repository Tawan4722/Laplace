param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

function Require-Command([string]$commandName, [string]$installHint) {
    $cmd = Get-Command $commandName -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw "$commandName was not found. $installHint"
    }
}

function Find-WindowsSdkTool([string]$toolName) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $sdkRoot)) {
        return $null
    }

    $candidate = Get-ChildItem -Path $sdkRoot -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($candidate) {
        return $candidate.FullName
    }

    return $null
}

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

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

Write-Host "==> Laplace setup started"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$dotnet = Resolve-Dotnet

$sdkVersionText = (& $dotnet --version).Trim()
$sdkVersion = [Version]$sdkVersionText
if ($sdkVersion.Major -lt 8) {
    throw ".NET SDK 8.0+ is required. Detected: $sdkVersionText"
}

Write-Host "==> .NET SDK: $sdkVersionText"
Write-Host "==> Restoring solution..."
Invoke-Checked -Label "dotnet restore" -Action {
    & $dotnet restore ".\Laplace.sln"
}

if (-not $SkipBuild) {
    Write-Host "==> Building solution ($Configuration)..."
    Invoke-Checked -Label "dotnet build" -Action {
        & $dotnet build ".\Laplace.sln" -c $Configuration --no-restore
    }
}
else {
    Write-Host "==> Build skipped"
}

if (-not $SkipTests) {
    Write-Host "==> Running tests ($Configuration)..."
    if ($SkipBuild) {
        Invoke-Checked -Label "dotnet test" -Action {
            & $dotnet test ".\Laplace.sln" -c $Configuration
        }
    }
    else {
        Invoke-Checked -Label "dotnet test" -Action {
            & $dotnet test ".\Laplace.sln" -c $Configuration --no-build
        }
    }
}
else {
    Write-Host "==> Tests skipped"
}

Write-Host "==> Optional packaging tool checks"

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

if ($isccCandidates.Count -gt 0) {
    Write-Host "    Inno Setup: found ($($isccCandidates[0]))"
}
else {
    Write-Host "    Inno Setup: missing (needed for installer/build-installer.ps1)"
}

$makeAppx = Find-WindowsSdkTool -toolName "makeappx.exe"
$signTool = Find-WindowsSdkTool -toolName "signtool.exe"

if ($makeAppx -and $signTool) {
    Write-Host "    Windows SDK tools: found"
}
else {
    Write-Host "    Windows SDK tools: missing makeappx.exe and/or signtool.exe (needed for installer/build-msix.ps1)"
}

Write-Host "==> Setup completed"
