# Laplace

![Laplace logo](assets/laplace-logo.png)

Laplace is a Windows-first archive manager with its own native `.lpc` format, adaptive block compression, password-protected archives, a command-line interface, a desktop app, and Explorer context-menu integration.

The `.lpc` format is implemented in this repository. It is not a wrapper around 7-Zip, WinRAR, or Windows shell archive commands.

## Current Status

Laplace is useful today as a native archive tool and archive-management shell for Windows, but it should not be marketed as a universal RAR replacement yet.

Compared with RAR, Laplace has stronger project transparency and a native adaptive per-block pipeline. RAR still has major advantages in maturity, compatibility, solid archiving, recovery records, multi-volume workflows, and proven compression behavior across many data sets. Laplace can beat RAR only on specific workloads after measurement, especially if the data benefits from Blosc2 or from configured external ZPAQ/BSC tools.

## Features

- Creates native `.lpc` archives.
- Creates `.zip`, `.7z`, and `.rar` archives.
- Extracts, lists, inspects, and tests common archive formats.
- Adds, freshens, deletes, renames, comments, locks, finds, and views native LPC archive entries.
- Delegates a safe subset of RAR mutations to installed WinRAR/RAR tools.
- Supports encrypted `.lpc` archives with AES-256-GCM payload encryption.
- Supports encrypted ZIP creation and password-aware ZIP read/test/extract paths.
- Chooses LPC compression per block from content analysis and trial compression.
- Stores incompressible LPC blocks as `RAW` instead of expanding them.
- Provides a Windows desktop UI for create, estimate, open/list, extract, and test workflows.
- Provides per-user Explorer context-menu integration.
- Builds self-contained Windows installer and MSIX artifacts.

## Supported Formats

Create:

- `.lpc`
- `.zip`
- `.7z`
- `.rar` when `rar.exe` or `WinRAR.exe` is installed

Read, list, info, test, and extract:

- `.lpc`
- `.zip`
- `.7z`
- `.rar`
- `.iso`
- `.tar`
- `.tar.gz`, `.tgz`
- `.tar.bz2`, `.tbz2`
- `.tar.xz`, `.txz`
- `.gz`
- `.bz2`
- `.xz`
- `.zst`
- `.lzip`

Unsupported formats fail with a clear error. For some non-password external archives on Windows, Laplace can fall back to the inbox `tar.exe`/libarchive path when the managed reader cannot extract them.

## Native LPC Format

`.lpc` archives use:

- Magic header `LPC1`
- LPCv1 for unencrypted archives
- LPCv2 for encrypted payload blocks
- A sequential data section
- File and block metadata tables
- Header CRC32C
- Per-block CRC32C
- Per-file SHA-256
- Optional AES-256-GCM payload encryption
- Optional locked archive flag in LPCv3

LPCv2 encryption protects block payload bytes. File names, sizes, timestamps, and table metadata remain visible so `list` and `info` can work without decrypting file contents.

LPCv3 is currently used for locked archives. Metadata encryption, recovery records, multi-volume archives, and solid archive layout are reserved and return explicit unsupported-feature errors until implemented.

See [docs/LPC_FORMAT.md](docs/LPC_FORMAT.md) for the binary layout.

## Compression

Laplace chooses LPC compression from:

- selected mode
- file extension hints
- entropy estimate
- repetition estimate
- pattern reuse
- text ratio
- zero-byte ratio
- trial-compressed sample sizes

Modes:

- `fast`: throughput-first
- `balanced`: default tradeoff
- `maximum`: size-focused
- `intensive`: strongest candidate set and ratio-focused scoring
- `compressed`: strongest ratio-first profile; for `.7z`, uses installed 7-Zip with solid LZMA2 when available; for `.rar`, uses installed WinRAR/RAR with RAR5, solid mode, and best compression
- `auto`: content-based candidate set

Methods:

- `RAW`
- `LZ4_FAST`
- `ZSTD_FAST`
- `ZSTD_BALANCED`
- `ZSTD_HIGH`
- `LZMA_MAX`
- `DEFLATE_FALLBACK`
- `BLOSC2`
- `ZPAQ`
- `BSC`

`BLOSC2` is built in through `Blosc2.PInvoke`. `ZPAQ` and `BSC` are optional external-command backends. They are only registered when both command templates are set:

- `LAPLACE_ZPAQ_COMPRESS_COMMAND`
- `LAPLACE_ZPAQ_DECOMPRESS_COMMAND`
- `LAPLACE_BSC_COMPRESS_COMMAND`
- `LAPLACE_BSC_DECOMPRESS_COMMAND`

Each template must read `{input}` and write `{output}`.

Important detail: `LZMA_MAX` is currently backed by Zstd level 19. It is not a true LZMA compressor yet.

See [docs/ADAPTIVE_COMPRESSION.md](docs/ADAPTIVE_COMPRESSION.md).

## Passwords And Encryption

CLI password inputs:

- `--password <value>`
- `--password-file <path>`
- interactive Windows popup when available
- console prompt fallback when available

Non-interactive runs must use `--password` or `--password-file`.

LPC encryption:

- AES-256-GCM per block
- PBKDF2-HMAC-SHA256 key derivation
- 600,000 default iterations for new archives
- bounded accepted iteration range: 210,000 to 5,000,000
- 32-byte generated salts
- per-block random nonce and authentication tag

ZIP encryption:

- AES-256 encrypted ZIP creation
- password-aware extraction, testing, listing, and info paths

## CLI

Run from source:

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- <command>
```

After install:

```powershell
laplace <command>
```

### Compress

```powershell
laplace compress <input_path...> [output.lpc|output.zip|output.7z|output.rar] `
  --mode fast|balanced|maximum|intensive|compressed|auto `
  --block-size 4M|8M|16M|32M|64M `
  --solid on|off|auto `
  --threads <number> `
  --verify|--no-verify
```

Examples:

```powershell
laplace compress .\folder .\archive.lpc --mode balanced --verify
laplace compress .\folder .\dataset.lpc --mode intensive
laplace compress .\folder .\archive.zip --mode maximum
laplace compress .\folder .\archive.7z --mode compressed --solid on
laplace compress .\folder .\archive.rar --mode compressed --solid on
laplace compress .\folder .\secure.lpc --encrypt
laplace compress .\folder .\secure.zip --password "secret"
laplace compress .\folder .\secure.lpc --password-file .\password.txt
```

With one input path and no explicit output path, `compress` creates an `.lpc` beside the input and uses a numbered fallback if the target already exists.

```powershell
laplace compress .\report.pdf --mode balanced
laplace compress-beside .\report.pdf --mode balanced
```

### Estimate

```powershell
laplace estimate <input_path...> `
  --mode fast|balanced|maximum|intensive|compressed|auto `
  --block-size 4M|8M|16M|32M|64M
```

The estimate command samples files, trial-compresses representative chunks, and reports estimated archive size, reduction, confidence, and likely methods.

### Extract

```powershell
laplace extract .\archive.lpc .\out --overwrite --no-verify --quiet
laplace extract .\image.iso .\usb-root --overwrite --no-verify
laplace extract .\secure.zip .\out --password "secret"
laplace extract .\secure.lpc .\out --password-file .\password.txt
```

### List, Info, And Test

```powershell
laplace list .\archive.lpc
laplace info .\archive.zip
laplace test .\secure.lpc --password "secret"
```

### Archive Management

```powershell
laplace add .\archive.lpc .\new-file.txt
laplace freshen .\archive.lpc .\new-file.txt
laplace delete .\archive.lpc old-file.txt
laplace rename .\archive.lpc old-name.txt renamed\new-name.txt
laplace comment .\archive.lpc --set "project backup"
laplace comment .\archive.lpc --show
laplace lock .\archive.lpc
laplace find .\archive.lpc --name "*.txt" --text "needle"
laplace view .\archive.lpc readme.txt
```

For `.lpc`, these commands use a safe full-rewrite path: extract to a temporary workspace, apply the change, create a replacement archive, verify it, then swap it into place. Locked LPC archives reject future mutation commands.

For `.rar`, Laplace delegates supported `add`, `freshen`, `delete`, `comment --set|--clear`, `lock`, and `repair` operations to installed WinRAR/RAR tools. Other formats are not mutated in place by Laplace.

### Desktop And Shell Helpers

```powershell
laplace open .\archive.lpc
laplace extract-here .\archive.lpc
laplace extract-to-folder .\archive.lpc .\destination
laplace extract-to-named-folder .\archive.lpc
laplace extract-dialog .\archive.lpc
```

### Benchmark

```powershell
laplace benchmark .\folder
```

## Desktop UI

Run from source:

```powershell
dotnet run --project .\src\Laplace.Desktop\Laplace.Desktop.csproj
```

The desktop app supports:

- create archive
- estimate compression
- open/list archive contents
- extract archive
- extract ISO contents to a removable drive
- extract selected entries
- delete selected entries from LPC archives
- test archive integrity
- password prompts

After install, launch Laplace from the Start Menu or desktop shortcut. Opening an `.lpc` file from Explorer also opens the desktop UI when shell integration is enabled.

## Explorer Integration

Install, inspect, or remove integration:

```powershell
laplace integrate install
laplace integrate status
laplace integrate uninstall
```

Shell integration is per-user and writes under `HKCU\Software\Classes`. It does not require administrator privileges.

It registers:

- `.lpc` file association
- archive actions: open, extract with options, extract here, extract to named folder, test integrity, show details
- archive actions also include find and repair verbs
- `.iso` actions include extracting ISO contents to a selected removable drive without formatting or raw-writing the drive
- create actions for files, folders, and folder background

See [docs/SHELL_INTEGRATION.md](docs/SHELL_INTEGRATION.md).

## Extraction Safety

Laplace blocks common unsafe archive paths:

- absolute paths
- path traversal paths
- Windows reserved device names
- alternate data stream syntax
- control characters
- unsafe trailing spaces or dots
- extraction through existing Windows reparse points

For LPC archives, Laplace also validates header framing, block offsets, block CRC32C, decompressed block sizes, file SHA-256, and AES-GCM authentication for encrypted payloads.

## Install

Download `LaplaceSetup.exe` from a GitHub Release and run it.

The installer:

- installs to `%LOCALAPPDATA%\Laplace`
- includes `laplace.exe`, `laplace-gui.exe`, docs, assets, and runtime dependencies
- can register `.lpc` association
- can add the Explorer context submenu

For local builds, the root `Setup.exe` file is generated output and should be refreshed from `artifacts\installer\LaplaceSetup.exe` when needed.

## Build From Source

Prerequisites:

- Windows
- .NET SDK 8.0+

Setup, build, and test:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

Manual commands:

```powershell
dotnet restore
dotnet build Laplace.sln
dotnet test tests\Laplace.Tests\Laplace.Tests.csproj
```

## Build Installer

Prerequisites:

- Inno Setup 6

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 1.0.0 `
  -SelfContained
```

Output:

- `artifacts\installer\LaplaceSetup.exe`

## Build MSIX

Prerequisites:

- Windows SDK with `makeappx.exe` and `signtool.exe`

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-msix.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 1.0.0.0 `
  -PackageName Laplace.Project `
  -Publisher "CN=LaplaceProject" `
  -SelfContained
```

Output:

- `artifacts\msix\Laplace_<version>_<runtime>.msix`

See [docs/MSIX.md](docs/MSIX.md).

## Verify Release

Run the full verification path before tagging:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-release.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 1.5.0 `
  -MsixVersion 1.5.0.0 `
  -SelfContained
```

This builds and tests the solution, builds installer and MSIX artifacts, generates checksums, silently installs the generated installer, runs archive smoke tests, verifies Windows `tar.exe` fallback extraction, and checks shell integration install/uninstall while restoring previous local registration.

## Repository Layout

```text
Laplace.sln
AGENT.md
assets/
docs/
installer/
scripts/
src/
  Laplace.Core/
  Laplace.Compression/
  Laplace.Cli/
  Laplace.Desktop/
  Laplace.ShellIntegration/
tests/
  Laplace.Tests/
```

## Release Process

Releases are automated by GitHub Actions.

```powershell
git tag -a v1.0.0 -m "Laplace v1.0.0"
git push origin main
git push origin v1.0.0
```

Release assets:

- `LaplaceSetup.exe`
- `Laplace_<version>_win-x64.msix`
- `SHA256SUMS.txt`

## License

GPLv3. See [LICENSE](LICENSE).
