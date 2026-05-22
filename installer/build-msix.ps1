param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0.0",
    [string]$PackageName = "Tawan4722.Laplace",
    [string]$DisplayName = "Laplace",
    [string]$PublisherDisplayName = "Laplace Project",
    [string]$Description = "Laplace archive and compression tool",
    [string]$Publisher = "CN=LaplaceDev",
    [string]$PfxPath = "",
    [string]$PfxPassword = "laplace-dev",
    [switch]$SelfContained,
    [switch]$InstallCertificate
)

$ErrorActionPreference = "Stop"

function Find-Tool([string]$toolName) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $sdkRoot)) {
        return $null
    }

    $candidates = Get-ChildItem -Path $sdkRoot -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Sort-Object FullName -Descending

    if ($candidates.Count -gt 0) {
        return $candidates[0].FullName
    }

    return $null
}

function Ensure-Dir([string]$path) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
}

function Write-PlaceholderPng([string]$path) {
    # 1x1 transparent PNG
    $base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9r7VQAAAAASUVORK5CYII="
    [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($base64))
}

function Ensure-CodeSigningCert([string]$certPublisher, [string]$targetPfx, [string]$password, [switch]$installCert) {
    if (Test-Path $targetPfx) {
        return
    }

    Write-Host "==> Creating development code-signing certificate..."
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $certPublisher `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyExportPolicy Exportable `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

    $securePass = ConvertTo-SecureString -String $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $targetPfx -Password $securePass | Out-Null

    if ($installCert) {
        Write-Host "==> Installing development certificate to TrustedPeople (CurrentUser)..."
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "CurrentUser")
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($cert)
        $store.Close()
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime-msix"
$msixRoot = Join-Path $repoRoot "artifacts\msix"
$stageDir = Join-Path $msixRoot "stage"
$assetsDir = Join-Path $stageDir "Assets"
$manifestTemplatePath = Join-Path $PSScriptRoot "msix\AppxManifest.template.xml"
$manifestOutPath = Join-Path $stageDir "AppxManifest.xml"
$packagePath = Join-Path $msixRoot "Laplace_$Version`_$Runtime.msix"
$certDir = Join-Path $msixRoot "cert"

Ensure-Dir $msixRoot
Ensure-Dir $certDir

if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    $PfxPath = Join-Path $certDir "LaplaceDev.pfx"
}

$makeAppx = Find-Tool "makeappx.exe"
$signTool = Find-Tool "signtool.exe"
if (-not $makeAppx) { throw "makeappx.exe not found. Install Windows 10/11 SDK." }
if (-not $signTool) { throw "signtool.exe not found. Install Windows 10/11 SDK." }

Write-Host "==> Publishing Laplace CLI for MSIX staging..."
if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

dotnet publish (Join-Path $repoRoot "src\Laplace.Cli\Laplace.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedValue `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

Write-Host "==> Preparing MSIX staging directory..."
if (Test-Path $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
Ensure-Dir $stageDir
Ensure-Dir $assetsDir

Copy-Item -Path (Join-Path $publishDir "*") -Destination $stageDir -Recurse -Force
Copy-Item -Path (Join-Path $repoRoot "README.md") -Destination $stageDir -Force

Write-PlaceholderPng (Join-Path $assetsDir "StoreLogo.png")
Write-PlaceholderPng (Join-Path $assetsDir "Square44x44Logo.png")
Write-PlaceholderPng (Join-Path $assetsDir "Square150x150Logo.png")

Write-Host "==> Generating AppxManifest.xml..."
$manifest = Get-Content -Path $manifestTemplatePath -Raw
$manifest = $manifest.Replace("{{PackageName}}", $PackageName)
$manifest = $manifest.Replace("{{Publisher}}", $Publisher)
$manifest = $manifest.Replace("{{PackageVersion}}", $Version)
$manifest = $manifest.Replace("{{DisplayName}}", $DisplayName)
$manifest = $manifest.Replace("{{PublisherDisplayName}}", $PublisherDisplayName)
$manifest = $manifest.Replace("{{Description}}", $Description)
[IO.File]::WriteAllText($manifestOutPath, $manifest, [Text.Encoding]::UTF8)

Ensure-CodeSigningCert -certPublisher $Publisher -targetPfx $PfxPath -password $PfxPassword -installCert:$InstallCertificate

if (Test-Path $packagePath) { Remove-Item -LiteralPath $packagePath -Force }

Write-Host "==> Packing MSIX..."
& $makeAppx pack /d $stageDir /p $packagePath /o | Out-Host

Write-Host "==> Signing MSIX..."
& $signTool sign /fd SHA256 /f $PfxPath /p $PfxPassword $packagePath | Out-Host

Write-Host "==> MSIX build completed."
Write-Host "Package: $packagePath"
Write-Host "PFX:     $PfxPath"
Write-Host "Install: Add-AppxPackage `"$packagePath`""
