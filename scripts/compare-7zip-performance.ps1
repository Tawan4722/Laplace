param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [ValidateSet("fast", "balanced", "maximum", "intensive", "compressed", "extreme", "auto")]
    [string]$LaplaceMode = "fast",

    [string]$SevenZipExe,

    [ValidateRange(0, 9)]
    [int]$SevenZipLevel = 9,

    [int]$Iterations = 3,

    [switch]$KeepArchives
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-SevenZipExecutable {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "7-Zip executable was not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    foreach ($name in @("7z.exe", "7za.exe")) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }

    foreach ($candidate in @(
        "C:\Program Files\7-Zip\7z.exe",
        "C:\Program Files (x86)\7-Zip\7z.exe"
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "7-Zip executable was not found. Install 7-Zip or pass -SevenZipExe 'C:\Path\To\7z.exe'."
}

function Get-InputSize {
    param([string]$Path)

    $item = Get-Item -LiteralPath $Path
    if (-not $item.PSIsContainer) {
        return $item.Length
    }

    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) {
        return 0
    }

    return [long]$sum
}

function Invoke-Tool {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $FileName @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "$FileName failed with exit code ${exitCode}: $($output.Trim())"
    }

    return $output.Trim()
}

function Measure-Tool {
    param([scriptblock]$Script)

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & $Script
    $watch.Stop()
    return $watch.Elapsed
}

function New-Result {
    param(
        [int]$Iteration,
        [string]$Tool,
        [long]$OriginalBytes,
        [long]$ArchiveBytes,
        [TimeSpan]$CompressTime,
        [TimeSpan]$ExtractTime
    )

    $ratio = if ($OriginalBytes -eq 0) { 0 } else { $ArchiveBytes / $OriginalBytes }
    $compressSpeed = if ($CompressTime.TotalSeconds -eq 0) { 0 } else { ($OriginalBytes / 1MB) / $CompressTime.TotalSeconds }
    $extractSpeed = if ($ExtractTime.TotalSeconds -eq 0) { 0 } else { ($OriginalBytes / 1MB) / $ExtractTime.TotalSeconds }

    [pscustomobject]@{
        Iteration = $Iteration
        Tool = $Tool
        OriginalBytes = $OriginalBytes
        ArchiveBytes = $ArchiveBytes
        RatioPercent = [math]::Round($ratio * 100, 2)
        CompressSeconds = [math]::Round($CompressTime.TotalSeconds, 3)
        CompressMiBPerSec = [math]::Round($compressSpeed, 2)
        ExtractSeconds = [math]::Round($ExtractTime.TotalSeconds, 3)
        ExtractMiBPerSec = [math]::Round($extractSpeed, 2)
    }
}

$resolvedInput = (Resolve-Path -LiteralPath $InputPath).Path
$repoRoot = Split-Path -Parent $PSScriptRoot
$laplaceProject = Join-Path $repoRoot "src\Laplace.Cli\Laplace.Cli.csproj"
$laplaceDll = Join-Path $repoRoot "src\Laplace.Cli\bin\Release\net8.0-windows\laplace.dll"
$sevenZipPath = Resolve-SevenZipExecutable -ExplicitPath $SevenZipExe
$originalBytes = Get-InputSize -Path $resolvedInput
$results = New-Object System.Collections.Generic.List[object]

if (-not (Test-Path -LiteralPath $laplaceDll -PathType Leaf)) {
    Invoke-Tool -FileName "dotnet" -Arguments @("build", $laplaceProject, "-c", "Release") -WorkingDirectory $repoRoot | Out-Null
}

for ($i = 1; $i -le $Iterations; $i++) {
    $runRoot = Join-Path ([System.IO.Path]::GetTempPath()) "laplace-vs-7zip-$([Guid]::NewGuid().ToString('N'))"
    $laplaceArchive = Join-Path $runRoot "laplace-$LaplaceMode.lpc"
    $laplaceOut = Join-Path $runRoot "laplace-out"
    $sevenZipArchive = Join-Path $runRoot "7zip-mx$SevenZipLevel.7z"
    $sevenZipOut = Join-Path $runRoot "7zip-out"
    New-Item -ItemType Directory -Path $runRoot, $laplaceOut, $sevenZipOut | Out-Null

    try {
        $laplaceCompress = Measure-Tool -Script {
            Invoke-Tool -FileName "dotnet" -Arguments @($laplaceDll, "compress", $resolvedInput, $laplaceArchive, "--mode", $LaplaceMode, "--no-verify", "--quiet") -WorkingDirectory $repoRoot | Out-Null
        }
        $laplaceExtract = Measure-Tool -Script {
            Invoke-Tool -FileName "dotnet" -Arguments @($laplaceDll, "extract", $laplaceArchive, $laplaceOut, "--overwrite", "--no-verify", "--quiet") -WorkingDirectory $repoRoot | Out-Null
        }
        $results.Add((New-Result -Iteration $i -Tool "Laplace $LaplaceMode" -OriginalBytes $originalBytes -ArchiveBytes (Get-Item -LiteralPath $laplaceArchive).Length -CompressTime $laplaceCompress -ExtractTime $laplaceExtract))

        $inputItem = Get-Item -LiteralPath $resolvedInput
        $sevenZipWorkingDirectory = if ($inputItem.PSIsContainer) {
            Split-Path -Parent $inputItem.FullName
        } else {
            $inputItem.DirectoryName
        }
        $sevenZipInputName = $inputItem.Name

        $sevenZipCompress = Measure-Tool -Script {
            Invoke-Tool -FileName $sevenZipPath -Arguments @("a", "-t7z", "-mx=$SevenZipLevel", "-m0=lzma2", "-mmt=on", $sevenZipArchive, $sevenZipInputName, "-r", "-y", "-bd") -WorkingDirectory $sevenZipWorkingDirectory | Out-Null
        }
        $sevenZipExtract = Measure-Tool -Script {
            Invoke-Tool -FileName $sevenZipPath -Arguments @("x", $sevenZipArchive, "-o$sevenZipOut", "-y", "-bd") -WorkingDirectory $runRoot | Out-Null
        }
        $results.Add((New-Result -Iteration $i -Tool "7-Zip mx=$SevenZipLevel LZMA2" -OriginalBytes $originalBytes -ArchiveBytes (Get-Item -LiteralPath $sevenZipArchive).Length -CompressTime $sevenZipCompress -ExtractTime $sevenZipExtract))
    }
    finally {
        if (-not $KeepArchives) {
            Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            Write-Host "Kept archives in $runRoot"
        }
    }
}

Write-Host "Input: $resolvedInput"
Write-Host "Original bytes: $originalBytes"
Write-Host "7-Zip executable: $sevenZipPath"
Write-Host ""
Write-Host "Runs:"
$results | Format-Table -AutoSize

Write-Host ""
Write-Host "Average:"
$results |
    Group-Object Tool |
    ForEach-Object {
        $rows = $_.Group
        [pscustomobject]@{
            Tool = $_.Name
            ArchiveBytes = [math]::Round(($rows | Measure-Object ArchiveBytes -Average).Average)
            RatioPercent = [math]::Round(($rows | Measure-Object RatioPercent -Average).Average, 2)
            CompressSeconds = [math]::Round(($rows | Measure-Object CompressSeconds -Average).Average, 3)
            CompressMiBPerSec = [math]::Round(($rows | Measure-Object CompressMiBPerSec -Average).Average, 2)
            ExtractSeconds = [math]::Round(($rows | Measure-Object ExtractSeconds -Average).Average, 3)
            ExtractMiBPerSec = [math]::Round(($rows | Measure-Object ExtractMiBPerSec -Average).Average, 2)
        }
    } |
    Format-Table -AutoSize
