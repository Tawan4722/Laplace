# Laplace

![Laplace logo](assets/laplace-logo.png)

Laplace is a Windows-first archive manager with a native `.lpc` format, a CLI, a WinForms desktop app, Explorer context-menu integration, packaging scripts, and a test suite.

The native `.lpc` format is implemented in this repository. Laplace is not a wrapper around 7-Zip, WinRAR, or Windows shell archive commands.

## What It Does

- Creates native `.lpc` archives.
- Creates `.zip`, `.7z`, and `.rar` archives.
- Extracts, lists, inspects, and tests common archive formats, including CAB packages.
- Adds, freshens, deletes, renames, comments, locks, finds, views, and repairs supported archives.
- Supports encrypted `.lpc` archives and password-aware ZIP workflows.
- Provides a desktop UI for create, estimate, open/list, extract, test, and ISO-to-removable-drive workflows.
- Provides per-user Explorer context-menu integration.
- Builds installer and MSIX release artifacts.

## Current Status

Laplace is practical today as a native archive tool and archive-management shell for Windows, but it should not be treated as a universal RAR or 7-Zip replacement.

The strongest parts of the project are transparency, native LPC control, path safety, and Windows integration. External archive formats are supported through managed libraries or installed command-line tools where that is the right tradeoff.

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
- `.cab`
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

Unsupported formats fail with a clear error. For some non-password external archives on Windows, Laplace can fall back to the inbox `tar.exe`/libarchive path when the managed reader cannot extract them. CAB archives are recognized in the UI and shell integration and are routed through the external archive path when the runtime can handle them.

## Native LPC Format

`.lpc` archives use:

- Magic header `LPC1`
- LPCv1 for unencrypted archives
- LPCv2 for encrypted payload blocks
- LPCv3 for locked archives
- LPCv4 for native solid archives
- LPCv5 for versioned Argon2id/PBKDF2 key derivation
- LPCv6 for encrypted file and block tables
- LPCv7 for Reed-Solomon recovery records
- Sequential data, file, and block metadata sections
- Header CRC32C
- Per-block CRC32C
- Per-file SHA-256
- Optional AES-256-GCM payload and metadata encryption
- Optional Reed-Solomon recovery data

New encrypted LPC archives use Argon2id by default. Older PBKDF2-HMAC-SHA256 archives remain readable, and LPCv5 records the selected KDF and its bounded parameters explicitly.

Payload encryption protects compressed block bytes. With `--hide-names`, LPCv6 also encrypts and authenticates file and block tables, so listing and archive details require the password. LPCv7 recovery records add striped Reed-Solomon parity that `laplace repair` can use without decrypting the archive. Multi-volume and SFX output remain reserved.

See [docs/LPC_FORMAT.md](docs/LPC_FORMAT.md) for the binary layout.

## Compression

Laplace chooses LPC compression from:

- compression mode
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

Important detail: `LZMA_MAX` now uses a real LZMA block compressor.

See [docs/ADAPTIVE_COMPRESSION.md](docs/ADAPTIVE_COMPRESSION.md).

## CLI

Run from source:

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- <command>
```

After install:

```powershell
laplace <command>
```

Common commands:

```powershell
laplace compress <input_path...> [output.lpc|output.zip|output.7z|output.rar] --mode fast|balanced|maximum|intensive|compressed|auto
laplace compress-beside <input_path> --mode balanced
laplace estimate <input_path...>
laplace extract <archive> <destination> --overwrite --no-verify
laplace list <archive>
laplace info <archive>
laplace test <archive>
laplace add <archive.lpc> <file-or-folder>
laplace freshen <archive.lpc> <file-or-folder>
laplace delete <archive.lpc> <entry...>
laplace rename <archive.lpc> <old> <new>
laplace comment <archive.lpc> --set "text"
laplace lock <archive.lpc>
laplace find <archive> --name "*.txt" --text "needle"
laplace view <archive> <entry>
laplace repair <archive.lpc|archive.rar>
laplace benchmark <input_path>
laplace open <archive>
laplace extract-here <archive>
laplace extract-to-folder <archive> <destination>
laplace extract-to-named-folder <archive>
laplace extract-dialog <archive>
laplace iso-to-drive-dialog <image.iso>
laplace integrate install|status|uninstall
```

Password inputs:

- `--password <value>`
- `--password-file <path>`
- `--encrypt` for prompting during create workflows

Non-interactive runs must use explicit password inputs.

LPC-specific creation options:

- `--hide-names` encrypts file and block tables and implies an encrypted archive.
- `--recovery-percent <1-100>` appends Reed-Solomon recovery data.
- `--threads <N>` controls parallel solid-block compression.

## Desktop UI

The desktop app supports:

- create archive
- estimate compression
- open/list archive contents
- extract archive
- extract selected entries
- delete selected LPC entries
- test archive integrity
- password prompts
- metadata-encryption and recovery-record creation options
- extract ISO contents to a removable drive

Opening an `.lpc` file from Explorer also opens the desktop UI when shell integration is enabled.

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
- archive actions for `.lpc`, `.zip`, `.7z`, `.rar`, `.cab`, `.iso`, `.tar`, `.gz`, `.tgz`, `.bz2`, `.xz`, `.zst`, and `.lzip`
- archive actions for open, extract with options, extract here, extract to named folder, test integrity, find, repair, and show details
- `.iso` actions that include extracting ISO contents to a selected removable drive without formatting or raw-writing the drive
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
  -Version <version> `
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
  -Version <version> `
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
  -Version <version> `
  -MsixVersion <version>.0 `
  -SelfContained
```

This builds and tests the solution, builds installer and MSIX artifacts, generates checksums, silently installs the generated installer, runs archive smoke tests, verifies Windows `tar.exe` fallback extraction, and checks shell integration install/uninstall while restoring previous local registration.

## Release Process

Releases are automated by GitHub Actions.

```powershell
git tag -a v<version> -m "Laplace v<version>"
git push origin main
git push origin v<version>
```

Release assets:

- `LaplaceSetup.exe`
- `Laplace_<version>_win-x64.msix`
- `SHA256SUMS.txt`

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

## License

GPLv3. See [LICENSE](LICENSE).
