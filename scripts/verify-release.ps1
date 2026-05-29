param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$MsixVersion = "",
    [string]$PackageName = "Laplace.Project",
    [string]$Publisher = "CN=LaplaceProject",
    [switch]$SelfContained,
    [switch]$SkipMsix,
    [switch]$SkipShellIntegrationSmoke
)

$ErrorActionPreference = "Stop"

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Laplace {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExePath,
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    Write-Host "==> Smoke: $Label"
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $output = & $ExePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "laplace $($Arguments -join ' ') failed with exit code ${exitCode}: $($output -join [Environment]::NewLine)"
    }

    return $output
}

function Get-RelativePathCompat([string]$RootPath, [string]$FullPath) {
    $rootFull = [IO.Path]::GetFullPath($RootPath).TrimEnd("\", "/") + [IO.Path]::DirectorySeparatorChar
    $fileFull = [IO.Path]::GetFullPath($FullPath)
    if (-not $fileFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside root: $FullPath"
    }

    return $fileFull.Substring($rootFull.Length)
}

function Assert-ContentMatch([string]$ExpectedRoot, [string]$ActualRoot) {
    foreach ($expected in Get-ChildItem -LiteralPath $ExpectedRoot -Recurse -File) {
        $relative = Get-RelativePathCompat $ExpectedRoot $expected.FullName
        $actual = Join-Path $ActualRoot $relative
        if (-not (Test-Path -LiteralPath $actual)) {
            throw "Missing extracted file: $relative"
        }

        $expectedHash = (Get-FileHash -LiteralPath $expected.FullName -Algorithm SHA256).Hash
        $actualHash = (Get-FileHash -LiteralPath $actual -Algorithm SHA256).Hash
        if ($expectedHash -ne $actualHash) {
            throw "Extracted file hash mismatch: $relative"
        }
    }
}

function New-SmokeSourceTree([string]$SourceRoot) {
    New-Item -ItemType Directory -Path (Join-Path $SourceRoot "nested") -Force | Out-Null
    Set-Content -Path (Join-Path $SourceRoot "hello.txt") `
        -Value ("Laplace release smoke" + [Environment]::NewLine + ("abc123 " * 2000)) `
        -Encoding UTF8
    Set-Content -Path (Join-Path $SourceRoot "nested\data.csv") `
        -Value @("id,value", "1,alpha", "2,beta", "3,gamma") `
        -Encoding UTF8
    [IO.File]::WriteAllBytes((Join-Path $SourceRoot "nested\bytes.bin"), ([byte[]](0..255)))
}

if ([string]::IsNullOrWhiteSpace($MsixVersion)) {
    $MsixVersion = "$Version.0"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerPath = Join-Path $repoRoot "artifacts\installer\LaplaceSetup.exe"
$msixPath = Join-Path $repoRoot "artifacts\msix\Laplace_$MsixVersion`_$Runtime.msix"
$checksumsPath = Join-Path $repoRoot "artifacts\SHA256SUMS.txt"
$validationRoot = Join-Path $repoRoot "artifacts\release-validation\v$Version"
$installDir = Join-Path $validationRoot "installed"
$smokeRoot = Join-Path $validationRoot "smoke"

Set-Location $repoRoot

Write-Host "==> Verifying source build and tests..."
Invoke-NativeCommand "setup.ps1" {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "setup.ps1") -Configuration $Configuration
}

Write-Host "==> Building installer..."
$installerArgs = @(
    "-ExecutionPolicy", "Bypass",
    "-File", (Join-Path $repoRoot "installer\build-installer.ps1"),
    "-Configuration", $Configuration,
    "-Runtime", $Runtime,
    "-Version", $Version
)
if ($SelfContained) {
    $installerArgs += "-SelfContained"
}
Invoke-NativeCommand "build-installer.ps1" {
    & powershell @installerArgs
}

if (-not $SkipMsix) {
    Write-Host "==> Building MSIX..."
    $msixArgs = @(
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $repoRoot "installer\build-msix.ps1"),
        "-Configuration", $Configuration,
        "-Runtime", $Runtime,
        "-Version", $MsixVersion,
        "-PackageName", $PackageName,
        "-Publisher", $Publisher
    )
    if ($SelfContained) {
        $msixArgs += "-SelfContained"
    }
    Invoke-NativeCommand "build-msix.ps1" {
        & powershell @msixArgs
    }
}

Write-Host "==> Generating release checksums..."
$checksumFiles = @($installerPath)
if (-not $SkipMsix) {
    $checksumFiles += $msixPath
}

$checksumLines = foreach ($file in $checksumFiles) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Missing release artifact: $file"
    }

    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Path $file -Leaf)"
}

$checksumDir = Split-Path -Parent $checksumsPath
if (-not (Test-Path -LiteralPath $checksumDir)) {
    New-Item -ItemType Directory -Path $checksumDir | Out-Null
}
$checksumLines | Set-Content -Path $checksumsPath -Encoding ASCII
Get-Content -Path $checksumsPath

Write-Host "==> Installing release installer for smoke tests..."
if (Test-Path -LiteralPath $validationRoot) {
    Remove-Item -LiteralPath $validationRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null

$installArgs = @(
    "/VERYSILENT",
    "/SUPPRESSMSGBOXES",
    "/NORESTART",
    "/NOICONS",
    "/DIR=$installDir",
    "/TASKS="
)
$process = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
    throw "Installer smoke install failed with exit code $($process.ExitCode)."
}

$laplaceExe = Join-Path $installDir "laplace.exe"
$laplaceGuiExe = Join-Path $installDir "laplace-gui.exe"
foreach ($required in @($laplaceExe, $laplaceGuiExe, (Join-Path $installDir "README.md"), (Join-Path $installDir "docs\LPC_FORMAT.md"))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Installed package missing required file: $required"
    }
}

Write-Host "==> Running CLI smoke tests..."
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
$sourceRoot = Join-Path $smokeRoot "source"
New-SmokeSourceTree $sourceRoot

$lpcPath = Join-Path $smokeRoot "sample.lpc"
Invoke-Laplace $laplaceExe "estimate" @("estimate", $sourceRoot, "--mode", "auto") | Out-Null
Invoke-Laplace $laplaceExe "compress lpc" @("compress", $sourceRoot, $lpcPath, "--mode", "balanced", "--verify") | Out-Null
Invoke-Laplace $laplaceExe "list lpc" @("list", $lpcPath) | Out-Null
Invoke-Laplace $laplaceExe "info lpc" @("info", $lpcPath) | Out-Null
Invoke-Laplace $laplaceExe "test lpc" @("test", $lpcPath) | Out-Null
$lpcOut = Join-Path $smokeRoot "out-lpc"
Invoke-Laplace $laplaceExe "extract lpc" @("extract", $lpcPath, $lpcOut, "--overwrite") | Out-Null
Assert-ContentMatch $sourceRoot (Join-Path $lpcOut "source")

$secureLpcPath = Join-Path $smokeRoot "secure.lpc"
Invoke-Laplace $laplaceExe "compress encrypted lpc" @("compress", $sourceRoot, $secureLpcPath, "--password", "release-smoke-secret", "--verify") | Out-Null
Invoke-Laplace $laplaceExe "test encrypted lpc" @("test", $secureLpcPath, "--password", "release-smoke-secret") | Out-Null
Invoke-Laplace $laplaceExe "reject wrong lpc password" @("test", $secureLpcPath, "--password", "wrong") @(2) | Out-Null

$zipPath = Join-Path $smokeRoot "secure.zip"
Invoke-Laplace $laplaceExe "compress encrypted zip" @("compress", $sourceRoot, $zipPath, "--password", "zip-smoke-secret", "--verify") | Out-Null
Invoke-Laplace $laplaceExe "test encrypted zip" @("test", $zipPath, "--password", "zip-smoke-secret") | Out-Null
$zipOut = Join-Path $smokeRoot "out-zip"
Invoke-Laplace $laplaceExe "extract encrypted zip" @("extract", $zipPath, $zipOut, "--overwrite", "--password", "zip-smoke-secret") | Out-Null
Assert-ContentMatch $sourceRoot (Join-Path $zipOut "source")

Write-Host "==> Running Windows tar fallback smoke test..."
$tarZstPath = Join-Path $smokeRoot "sample.tar.zst"
Push-Location $smokeRoot
try {
    Invoke-NativeCommand "tar --zstd" {
        & tar --zstd -cf $tarZstPath source
    }
}
finally {
    Pop-Location
}
$tarZstOut = Join-Path $smokeRoot "out-tar-zst"
Invoke-Laplace $laplaceExe "extract tar.zst fallback" @("extract", $tarZstPath, $tarZstOut, "--overwrite") | Out-Null
Assert-ContentMatch $sourceRoot (Join-Path $tarZstOut "source")

if (-not $SkipShellIntegrationSmoke) {
    Write-Host "==> Running shell integration smoke test..."
    Invoke-Laplace $laplaceExe "integrate uninstall before smoke" @("integrate", "uninstall") @(0) | Out-Null
    Invoke-Laplace $laplaceExe "integrate status clean" @("integrate", "status") | Out-Null
    Invoke-Laplace $laplaceExe "integrate install" @("integrate", "install", "--cli-path", $laplaceExe) | Out-Null
    $status = Invoke-Laplace $laplaceExe "integrate status installed" @("integrate", "status")
    if (($status -join [Environment]::NewLine) -notmatch "Installed:\s+True") {
        throw "Shell integration did not report installed after install."
    }
    Invoke-Laplace $laplaceExe "integrate uninstall" @("integrate", "uninstall") | Out-Null
    $finalStatus = Invoke-Laplace $laplaceExe "integrate status clean after uninstall" @("integrate", "status")
    if (($finalStatus -join [Environment]::NewLine) -notmatch "Installed:\s+False") {
        throw "Shell integration did not report clean after uninstall."
    }
}

Write-Host "==> Release verification completed."
Write-Host "Installer: $installerPath"
if (-not $SkipMsix) {
    Write-Host "MSIX:      $msixPath"
}
Write-Host "Checksums: $checksumsPath"
