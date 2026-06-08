param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [string]$RarExe,

    [int]$Iterations = 1,

    [switch]$KeepArchives
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RarExecutable {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "RAR executable was not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    foreach ($name in @("rar.exe", "WinRAR.exe")) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }

    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        foreach ($name in @("rar.exe", "WinRAR.exe")) {
            $candidate = Join-Path $root "WinRAR\$name"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    return $null
}

function Get-InputSize {
    param([string]$Path)

    $item = Get-Item -LiteralPath $Path
    if (-not $item.PSIsContainer) {
        return $item.Length
    }

    return (Get-ChildItem -LiteralPath $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
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
    param(
        [string]$Name,
        [scriptblock]$Script
    )

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & $Script
    $watch.Stop()
    return $watch.Elapsed
}

function New-Result {
    param(
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
        Tool = $Tool
        OriginalBytes = $OriginalBytes
        ArchiveBytes = $ArchiveBytes
        Ratio = "{0:P2}" -f $ratio
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
$rarPath = Resolve-RarExecutable -ExplicitPath $RarExe
$originalBytes = [long](Get-InputSize -Path $resolvedInput)
$results = New-Object System.Collections.Generic.List[object]

if (-not (Test-Path -LiteralPath $laplaceDll -PathType Leaf)) {
    Invoke-Tool -FileName "dotnet" -Arguments @("build", $laplaceProject, "-c", "Release") -WorkingDirectory $repoRoot | Out-Null
}

for ($i = 1; $i -le $Iterations; $i++) {
    $runRoot = Join-Path ([System.IO.Path]::GetTempPath()) "laplace-vs-winrar-$([Guid]::NewGuid().ToString('N'))"
    $laplaceArchive = Join-Path $runRoot "laplace-intensive.lpc"
    $laplaceOut = Join-Path $runRoot "laplace-out"
    $rarArchive = Join-Path $runRoot "winrar-m5.rar"
    $rarOut = Join-Path $runRoot "rar-out"
    New-Item -ItemType Directory -Path $runRoot, $laplaceOut, $rarOut | Out-Null

    try {
        $laplaceCompress = Measure-Tool -Name "Laplace compress" -Script {
            Invoke-Tool -FileName "dotnet" -Arguments @($laplaceDll, "compress", $resolvedInput, $laplaceArchive, "--mode", "intensive", "--no-verify") -WorkingDirectory $repoRoot | Out-Null
        }
        $laplaceExtract = Measure-Tool -Name "Laplace extract" -Script {
            Invoke-Tool -FileName "dotnet" -Arguments @($laplaceDll, "extract", $laplaceArchive, $laplaceOut, "--overwrite") -WorkingDirectory $repoRoot | Out-Null
        }
        $results.Add((New-Result -Tool "Laplace intensive" -OriginalBytes $originalBytes -ArchiveBytes (Get-Item -LiteralPath $laplaceArchive).Length -CompressTime $laplaceCompress -ExtractTime $laplaceExtract))

        if ($null -ne $rarPath) {
            $inputItem = Get-Item -LiteralPath $resolvedInput
            $rarWorkingDirectory = if ($inputItem.PSIsContainer) {
                Split-Path -Parent $inputItem.FullName
            } else {
                $inputItem.DirectoryName
            }
            $rarInputName = $inputItem.Name

            $rarCompress = Measure-Tool -Name "WinRAR compress" -Script {
                Invoke-Tool -FileName $rarPath -Arguments @("a", "-r", "-o+", "-idq", "-m5", $rarArchive, $rarInputName) -WorkingDirectory $rarWorkingDirectory | Out-Null
            }
            $rarExtract = Measure-Tool -Name "WinRAR extract" -Script {
                Invoke-Tool -FileName $rarPath -Arguments @("x", "-o+", "-idq", $rarArchive, "$rarOut\") -WorkingDirectory $runRoot | Out-Null
            }
            $results.Add((New-Result -Tool "WinRAR/RAR -m5" -OriginalBytes $originalBytes -ArchiveBytes (Get-Item -LiteralPath $rarArchive).Length -CompressTime $rarCompress -ExtractTime $rarExtract))
        }
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
if ($null -eq $rarPath) {
    Write-Host "WinRAR/RAR executable not found. Pass -RarExe 'C:\Path\To\Rar.exe' after installing WinRAR."
} else {
    Write-Host "WinRAR/RAR executable: $rarPath"
}

$results | Format-Table -AutoSize
