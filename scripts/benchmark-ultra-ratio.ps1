param(
    [string]$OutputDirectory = "",
    [int]$Iterations = 1,
    [string]$SevenZipExe = "",
    [string]$RarExe = "",
    [switch]$SkipSevenZip,
    [switch]$SkipRar,
    [switch]$KeepArchives
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-Executable {
    param(
        [string]$ExplicitPath,
        [string[]]$CommandNames,
        [string[]]$Candidates
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Executable was not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    foreach ($name in $CommandNames) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }

    foreach ($candidate in $Candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $null
}

function Write-RandomBytes {
    param(
        [System.IO.Stream]$Stream,
        [long]$ByteCount,
        [int]$Seed
    )

    $random = [System.Random]::new($Seed)
    $buffer = [byte[]]::new(1MB)
    $remaining = $ByteCount
    while ($remaining -gt 0) {
        $count = [int][Math]::Min($buffer.Length, $remaining)
        $random.NextBytes($buffer)
        $Stream.Write($buffer, 0, $count)
        $remaining -= $count
    }
}

function New-BenchmarkCorpus {
    param([string]$CorpusPath)

    if (Test-Path -LiteralPath $CorpusPath -PathType Container) {
        $expectedFiles = @("distant-repeat.bin", "entropy.bin", "structured.log")
        $actualFiles = @(Get-ChildItem -LiteralPath $CorpusPath -File | Select-Object -ExpandProperty Name)
        $unexpectedFiles = @($actualFiles | Where-Object { $_ -notin $expectedFiles })
        $missingFiles = @($expectedFiles | Where-Object { $_ -notin $actualFiles })
        if ($unexpectedFiles.Count -gt 0 -or $missingFiles.Count -gt 0) {
            throw "Benchmark corpus is not clean. Missing: $($missingFiles -join ', '); unexpected: $($unexpectedFiles -join ', ')."
        }
        return
    }

    New-Item -ItemType Directory -Path $CorpusPath -Force | Out-Null

    $segment = [byte[]]::new(16MB)
    [System.Random]::new(4722).NextBytes($segment)
    $distantPath = Join-Path $CorpusPath "distant-repeat.bin"
    $stream = [System.IO.File]::Create($distantPath)
    try {
        $stream.Write($segment, 0, $segment.Length)
        Write-RandomBytes -Stream $stream -ByteCount 80MB -Seed 193
        $stream.Write($segment, 0, $segment.Length)
    }
    finally {
        $stream.Dispose()
    }

    $line = [Text.Encoding]::UTF8.GetBytes(
        "2026-06-07T00:00:00Z INFO request completed route=/archive/create codec=lzma status=200 duration_ms=183`r`n")
    $textBuffer = [byte[]]::new(1MB)
    for ($offset = 0; $offset -lt $textBuffer.Length; $offset += $line.Length) {
        [Array]::Copy($line, 0, $textBuffer, $offset, [Math]::Min($line.Length, $textBuffer.Length - $offset))
    }
    $structuredPath = Join-Path $CorpusPath "structured.log"
    $stream = [System.IO.File]::Create($structuredPath)
    try {
        for ($i = 0; $i -lt 48; $i++) {
            $stream.Write($textBuffer, 0, $textBuffer.Length)
        }
    }
    finally {
        $stream.Dispose()
    }

    $entropyPath = Join-Path $CorpusPath "entropy.bin"
    $stream = [System.IO.File]::Create($entropyPath)
    try {
        Write-RandomBytes -Stream $stream -ByteCount 32MB -Seed 919
    }
    finally {
        $stream.Dispose()
    }
}

function ConvertTo-WindowsCommandLineArgument {
    param([string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append('\' * ($backslashes * 2 + 1))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append('\' * $backslashes)
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$builder.Append('\' * ($backslashes * 2))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-MeasuredProcess {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$LogPrefix
    )

    $stdoutPath = "$LogPrefix.stdout.txt"
    $stderrPath = "$LogPrefix.stderr.txt"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $false
    $startInfo.RedirectStandardError = $false
    $startInfo.Arguments = (($Arguments | ForEach-Object {
        ConvertTo-WindowsCommandLineArgument -Value $_
    }) -join " ")

    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw "Could not start $FileName."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    [long]$peakWorkingSet = 0
    while (-not $process.HasExited) {
        try {
            $process.Refresh()
            $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.WorkingSet64)
        }
        catch {
        }
        Start-Sleep -Milliseconds 50
    }

    $process.WaitForExit()
    $watch.Stop()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText($stdoutPath, $stdout)
    [IO.File]::WriteAllText($stderrPath, $stderr)

    if ($process.ExitCode -ne 0) {
        throw "$FileName failed with exit code $($process.ExitCode). See $stdoutPath and $stderrPath."
    }

    return [pscustomobject]@{
        ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        PeakWorkingSetBytes = $peakWorkingSet
    }
}

function New-Result {
    param(
        [int]$Iteration,
        [string]$Tool,
        [string]$Settings,
        [string]$Method,
        [long]$OriginalBytes,
        [string]$ArchivePath,
        [pscustomobject]$Measurement
    )

    $archiveBytes = (Get-Item -LiteralPath $ArchivePath).Length
    return [pscustomobject]@{
        Iteration = $Iteration
        Tool = $Tool
        Settings = $Settings
        Method = $Method
        OriginalBytes = $OriginalBytes
        ArchiveBytes = $archiveBytes
        RatioPercent = [Math]::Round(($archiveBytes / $OriginalBytes) * 100, 3)
        SavingsPercent = [Math]::Round((1 - ($archiveBytes / $OriginalBytes)) * 100, 3)
        CompressSeconds = $Measurement.ElapsedSeconds
        CompressMiBPerSecond = [Math]::Round(($OriginalBytes / 1MB) / $Measurement.ElapsedSeconds, 2)
        PeakWorkingSetMiB = [Math]::Round($Measurement.PeakWorkingSetBytes / 1MB, 1)
    }
}

if ($Iterations -lt 1) {
    throw "Iterations must be at least 1."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "artifacts\benchmarks\ultra-ratio-$stamp"
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$corpusPath = Join-Path $outputRoot "corpus"
$archiveRoot = Join-Path $outputRoot "archives"
$logRoot = Join-Path $outputRoot "logs"
New-Item -ItemType Directory -Path $archiveRoot, $logRoot -Force | Out-Null
New-BenchmarkCorpus -CorpusPath $corpusPath

$laplaceDll = Join-Path $repoRoot "src\Laplace.Cli\bin\Release\net8.0-windows\laplace.dll"
if (-not (Test-Path -LiteralPath $laplaceDll -PathType Leaf)) {
    & dotnet build (Join-Path $repoRoot "Laplace.sln") -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Laplace Release build failed."
    }
}

$sevenZipPath = Resolve-Executable -ExplicitPath $SevenZipExe `
    -CommandNames @("7z.exe", "7za.exe") `
    -Candidates @("C:\Program Files\7-Zip\7z.exe", "C:\Program Files (x86)\7-Zip\7z.exe")
$rarPath = Resolve-Executable -ExplicitPath $RarExe `
    -CommandNames @("rar.exe", "WinRAR.exe") `
    -Candidates @("C:\Program Files\WinRAR\Rar.exe", "D:\WinRAR\Rar.exe")

$originalBytes = [long](Get-ChildItem -LiteralPath $corpusPath -File | Measure-Object Length -Sum).Sum
$results = [Collections.Generic.List[object]]::new()
$corpusParent = Split-Path -Parent $corpusPath
$corpusName = Split-Path -Leaf $corpusPath

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    foreach ($mode in @("compressed", "extreme")) {
        $archivePath = Join-Path $archiveRoot "laplace-$mode-$iteration.lpc"
        $measurement = Invoke-MeasuredProcess `
            -FileName "dotnet" `
            -Arguments @($laplaceDll, "compress", $corpusPath, $archivePath, "--mode", $mode, "--no-verify", "--quiet") `
            -WorkingDirectory $repoRoot `
            -LogPrefix (Join-Path $logRoot "laplace-$mode-$iteration")
        & dotnet $laplaceDll test $archivePath --quiet
        if ($LASTEXITCODE -ne 0) {
            throw "Laplace verification failed for $archivePath."
        }
        $archiveInfo = (& dotnet $laplaceDll info $archivePath --json | ConvertFrom-Json).info
        $results.Add((New-Result -Iteration $iteration -Tool "Laplace $mode" -Settings "--mode $mode" `
            -Method ($archiveInfo.methodsUsed -join ",") `
            -OriginalBytes $originalBytes -ArchivePath $archivePath -Measurement $measurement))
    }

    if (-not $SkipSevenZip -and $null -ne $sevenZipPath) {
        $archivePath = Join-Path $archiveRoot "7zip-ultra-$iteration.7z"
        $measurement = Invoke-MeasuredProcess `
            -FileName $sevenZipPath `
            -Arguments @("a", "-t7z", "-mx=9", "-m0=lzma2", "-md=128m", "-mfb=273", "-ms=on", "-mmt=1", "-y", "-bd", $archivePath, $corpusName) `
            -WorkingDirectory $corpusParent `
            -LogPrefix (Join-Path $logRoot "7zip-ultra-$iteration")
        & $sevenZipPath t $archivePath -y -bd | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip verification failed for $archivePath."
        }
        $results.Add((New-Result -Iteration $iteration -Tool "7-Zip Ultra" `
            -Settings "-mx=9 -md=128m -mfb=273 -ms=on -mmt=1" `
            -Method "LZMA2" `
            -OriginalBytes $originalBytes -ArchivePath $archivePath -Measurement $measurement))
    }

    if (-not $SkipRar -and $null -ne $rarPath) {
        $archivePath = Join-Path $archiveRoot "winrar-best-$iteration.rar"
        $measurement = Invoke-MeasuredProcess `
            -FileName $rarPath `
            -Arguments @("a", "-r", "-o+", "-idq", "-ma5", "-m5", "-md128m", "-s", "-mt1", $archivePath, $corpusName) `
            -WorkingDirectory $corpusParent `
            -LogPrefix (Join-Path $logRoot "winrar-best-$iteration")
        & $rarPath t -idq $archivePath | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "WinRAR verification failed for $archivePath."
        }
        $results.Add((New-Result -Iteration $iteration -Tool "WinRAR Best" `
            -Settings "-ma5 -m5 -md128m -s -mt1" `
            -Method "RAR5" `
            -OriginalBytes $originalBytes -ArchivePath $archivePath -Measurement $measurement))
    }
}

$csvPath = Join-Path $outputRoot "results.csv"
$jsonPath = Join-Path $outputRoot "results.json"
$environmentPath = Join-Path $outputRoot "environment.json"
$results | Export-Csv -LiteralPath $csvPath -NoTypeInformation
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath
[pscustomobject]@{
    Timestamp = (Get-Date).ToString("o")
    Machine = $env:COMPUTERNAME
    OS = [Environment]::OSVersion.VersionString
    Processor = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
    LogicalProcessors = [Environment]::ProcessorCount
    TotalPhysicalMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    DotNet = (& dotnet --version)
    LaplaceCommit = (& git -C $repoRoot rev-parse HEAD)
    SevenZip = $sevenZipPath
    WinRar = $rarPath
    OriginalBytes = $originalBytes
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $environmentPath

$results | Sort-Object Iteration, ArchiveBytes | Format-Table -AutoSize
Write-Host ""
Write-Host "Results: $csvPath"
Write-Host "Environment: $environmentPath"

if (-not $KeepArchives) {
    Remove-Item -LiteralPath $archiveRoot -Recurse -Force
}
