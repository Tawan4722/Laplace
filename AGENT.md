# Laplace Agent Guide

This is the operating guide for agents working in the Laplace repository. Keep it aligned with the codebase, not with assumptions.

## What This Repo Is

Laplace is a Windows-first archive manager written in C#/.NET 8. It includes:

- native `.lpc` archives
- a CLI
- a WinForms desktop app
- Explorer context-menu integration
- packaging scripts
- tests

The native `.lpc` format is implemented in this repository. Laplace is not a wrapper around 7-Zip, WinRAR, or Windows shell archive commands. Non-LPC formats are handled through managed libraries or installed external tools where that is the correct tradeoff.

## Read First

The files below explain most of the project surface:

- `src/Laplace.Core/Services/ArchiveFormatDetector.cs`
- `src/Laplace.Core/Services/UniversalArchiveService.cs`
- `src/Laplace.Core/Services/ArchiveWriter.cs`
- `src/Laplace.Core/Services/ArchiveExtractor.cs`
- `src/Laplace.Core/Compression/AdaptiveCompressionEngine.cs`
- `src/Laplace.Core/Services/WindowsNativeArchiveHandler.cs`
- `src/Laplace.Core/Services/SharpCompressArchiveHandler.cs`
- `src/Laplace.Core/Services/SevenZipArchiveWriter.cs`
- `src/Laplace.Core/Services/RarArchiveWriter.cs`
- `src/Laplace.Cli/Program.cs`
- `src/Laplace.Desktop/MainForm.cs`
- `src/Laplace.ShellIntegration/ShellIntegrationManager.cs`
- `tests/Laplace.Tests/ArchiveRoundTripTests.cs`

## Architecture Notes

### LPC

`.lpc` is the native format. It supports:

- header validation
- block and file metadata
- per-block CRC32C
- per-file SHA-256
- optional AES-256-GCM payload encryption
- optional keyfile-based encryption context for LPC archives
- locked archives through LPCv3
- native solid archive layout through LPCv4
- versioned Argon2id/PBKDF2 key derivation through LPCv5
- authenticated metadata encryption through LPCv6
- Reed-Solomon recovery records through LPCv7

Reserved but not implemented:

- multi-volume archives
- SFX output

Do not describe those as implemented unless the code changes.

### Compression

LPC compression is adaptive and block-oriented. The engine samples data, analyzes extension hints and byte statistics, trial-compresses candidates, scores them, and stores blocks as `RAW` when compression would expand them.

Current compression modes:

- `fast`
- `balanced`
- `maximum`
- `intensive`
- `compressed`
- `extreme`
- `auto`

Important implementation fact: `LZMA_MAX` is now backed by a real LZMA block compressor. It is no longer aliased to Zstd.
`extreme` is LPC-only and automatically selects 16-256 MiB blocks with tiered 8-128 MiB Zstd long-distance windows and LZMA dictionaries. It forces one compression worker and rejects explicit block sizes.

Built-in or optional methods:

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
- `CMIX`

`BLOSC2` is built in. `ZPAQ`, `BSC`, and `CMIX` are only registered when the required command templates are configured in the environment. `CMIX` is additionally size-gated: it is only offered as a candidate when total input size is ≥ 20 GB and the mode is `intensive`, `compressed`, or `extreme`.

### Format Routing

Write support today:

- `.lpc`: native writer
- `.zip`: SharpZipLib writer with AES-256 ZIP support when a password is supplied
- `.7z`: SharpCompress writer, with external 7-Zip used when the mode or options require it
- `.rar`: delegates to installed `rar.exe` or `WinRAR.exe`

Read/list/info/test/extract support today:

- `.lpc`: native services
- `.zip`: ZIP handler
- `.7z`, `.rar`, `.cab`, tar variants, `.gz`, `.bz2`, `.xz`, `.zst`, `.lzip`, and `.iso`: external or native helper paths depending on the archive and runtime availability

### Safety

Path safety lives in `PathSecurity`. Preserve these checks:

- reject absolute archive paths
- reject traversal paths
- reject alternate data streams
- reject control characters
- reject Windows reserved names
- reject unsafe trailing spaces and dots
- block extraction through existing Windows reparse points

### Current CLI State

The CLI has working support for:

- `--json` on major reporting and automation commands
- `--dry-run` on create, extract, and mutation workflows
- `--from-file` for newline-delimited operand lists on batch-oriented commands
- glob-driven `find`, `extract`, and LPC `delete`
- `diff`, `merge`, and `split`
- shell completion scripts in `scripts/completions`

Important behavioral limits:

- `view` writes raw bytes to stdout and is not JSON-wrapped
- structured progress output for scripting is not implemented yet
- `--json` is broad but not literally present on every command

### Current Encryption State

LPC encryption today supports:

- password-only archives
- keyfile-only archives
- password + keyfile archives
- fixed-time confirmation comparison during interactive password confirmation

Important implementation fact:

- new LPC encrypted archives use Argon2id by default with bounded time, memory, and parallelism parameters
- LPCv2-v4 PBKDF2-HMAC-SHA256 archives remain readable, and LPCv5 can explicitly identify either KDF
- keyfiles are supported for LPC archives only
- ZIP, 7z, RAR, and other non-LPC paths reject keyfile-based encryption options
- payload blocks use AES-256-GCM; LPCv6 can also encrypt and authenticate file and block tables
- solid LPC is single-stream and works for create, list, extract, test, and rewrite-style mutation
- solid blocks can be compressed concurrently while preserving deterministic block order
- LPCv7 recovery records use striped Reed-Solomon parity and can repair damaged protected shards without a password

## To Do

Completed format-level backlog after native LPC solid layout:

- [x] Argon2id key-derivation versioning for LPC encrypted archives
- [x] Metadata encryption for LPC file and block tables
- [x] Multi-threaded solid block compression on top of the LPCv4 solid layout
- [x] Recovery records and Reed-Solomon repair data

Remaining reserved format work:

- [ ] Multi-volume LPC output
- [ ] SFX output

## Change Rules

When changing supported formats, update all of these together:

- detector / routing code
- desktop UI filters and archive checks
- shell integration menus
- tests
- README

If the change affects behavior that is exposed to users, do not leave the docs behind. Keep the file lists and command examples in sync with the code.

When a feature depends on installed external tools, say so explicitly. Do not imply built-in support where it does not exist.

## Operational Limits

Do not claim:

- Laplace is generally better than RAR or 7-Zip without a benchmark
- `ZPAQ` or `BSC` are built in
- CAB has a native first-class writer
- multi-volume LPC is implemented
- SFX LPC output is implemented

If a change touches release packaging, verify the release script and installer/MSIX paths as well.

## Build And Test

Common commands:

```powershell
dotnet restore
dotnet build Laplace.sln
dotnet test tests\Laplace.Tests\Laplace.Tests.csproj
```

Release verification:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-release.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version <version> `
  -MsixVersion <version>.0 `
  -SelfContained
```

Installer builds require Inno Setup 6. MSIX builds require Windows SDK tooling.

## Working Style

- Inspect the code before rewriting docs or changing behavior.
- Prefer the repository's existing patterns over new abstractions.
- Keep edits tight to the feature being changed.
- Use tests to lock in routing, format detection, and safety behavior.
- Do not revert unrelated user changes.
- Do not make claims in docs that the code does not support.

## Release Notes

GitHub Actions publishes release assets on tag pushes matching `v*`.

Release artifacts are expected to include:

- `LaplaceSetup.exe`
- `Laplace_<version>_win-x64.msix`
- `SHA256SUMS.txt`
