# Laplace Agent Guide

This file is a working guide for agents and maintainers touching the Laplace repository.

## Project Summary

Laplace is a Windows-first archive manager written in C#/.NET 8. It has a native `.lpc` archive format, a command-line app, a WinForms desktop app, Explorer shell integration, packaging scripts, and tests.

The native `.lpc` format is implemented in this repository. It is not a wrapper around 7-Zip, WinRAR, or Windows shell commands. Non-LPC formats are handled through separate managed libraries or external tooling.

## Solution Layout

- `src/Laplace.Core`: archive model, LPC reader/writer/extractor/tester, format routing, encryption, path safety, adaptive compression analysis.
- `src/Laplace.Compression`: block compressor registry and implementations.
- `src/Laplace.Cli`: command-line entry point, command parsing, password providers, desktop-launch helpers.
- `src/Laplace.Desktop`: Windows Forms UI for create, estimate, open/list, extract, and test workflows.
- `src/Laplace.ShellIntegration`: per-user HKCU Explorer context-menu and `.lpc` association management.
- `tests/Laplace.Tests`: unit, integration, and CLI black-box tests.
- `docs`: LPC format, adaptive compression, shell integration, and MSIX notes.
- `installer`: Inno Setup and MSIX build scripts.
- `scripts/verify-release.ps1`: full release verification path.

## Native LPC Behavior

`.lpc` archives use:

- Magic header `LPC1`.
- Format version `1` for unencrypted archives.
- Format version `2` for encrypted payload blocks.
- A data section followed by file and block tables.
- Per-block compression method metadata.
- Header CRC32C.
- Per-block CRC32C over stored bytes.
- Per-file SHA-256 checksums.
- Optional AES-256-GCM payload encryption.
- Optional LPCv3 locked archive flag.

Encryption protects block payload bytes only. File names, sizes, timestamps, and table metadata remain visible so `list` and `info` can work without decrypting payloads.

LPCv3 currently exists for locked archives. Metadata encryption, recovery records, multi-volume output, and solid archive layout are reserved behind explicit unsupported-feature errors.

## Compression Model

LPC compression is adaptive and block-oriented. For each block, Laplace samples up to `64 KiB`, analyzes extension hints and byte statistics, trial-compresses candidate methods, scores them, and uses the best shrinking result. If the selected method fails or the compressed block is not smaller than the original, the block is stored as `RAW`.

Compression modes:

- `fast`: throughput-first.
- `balanced`: default tradeoff.
- `maximum`: size-focused.
- `intensive`: strongest candidate set and ratio-focused scoring.
- `auto`: content-based candidate set.

Compression methods:

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

Important implementation detail: `LZMA_MAX` is currently backed by Zstd level 19, not a true LZMA implementation.

`BLOSC2` is built in through `Blosc2.PInvoke`. `ZPAQ` and `BSC` are optional external-command adapters. They are registered only when both command templates are configured:

- `LAPLACE_ZPAQ_COMPRESS_COMMAND`
- `LAPLACE_ZPAQ_DECOMPRESS_COMMAND`
- `LAPLACE_BSC_COMPRESS_COMMAND`
- `LAPLACE_BSC_DECOMPRESS_COMMAND`

Each template must read `{input}` and write `{output}`.

## Format Routing

Write support:

- `.lpc`: native writer.
- `.zip`: SharpZipLib writer, AES-256 ZIP encryption when a password is supplied.
- `.7z`: SharpCompress 7z writer using LZMA; no managed encryption support.
- `.rar`: delegates to installed `rar.exe` or `WinRAR.exe`.

Read/list/info/test/extract support:

- `.lpc`: native services.
- `.zip`: ZIP handler.
- `.7z`, `.rar`, tar and compressed tar variants, `.gz`, `.bz2`, `.xz`, `.zst`, `.lzip`: managed external handler, with Windows `tar.exe` fallback for some unsupported non-password extraction cases.

## Security Boundaries

Extraction safety is centralized in `PathSecurity` and must be preserved:

- Reject absolute archive paths.
- Reject traversal segments.
- Reject control characters.
- Reject Windows reserved device names.
- Reject alternate data stream syntax.
- Reject unsafe trailing spaces or dots.
- Prevent extraction through existing Windows reparse points.

Encrypted LPC archives use PBKDF2-HMAC-SHA256 with bounded iteration counts. New encrypted archives default to `600,000` iterations and 32-byte salts.

## CLI Commands

Main commands:

- `compress`
- `compress-beside`
- `estimate`
- `extract`
- `list`
- `info`
- `test`
- `add`
- `freshen`
- `delete`
- `rename`
- `comment`
- `lock`
- `find`
- `view`
- `repair`
- `benchmark`
- `open`
- `extract-here`
- `extract-to-folder`
- `extract-to-named-folder`
- `extract-dialog`
- `compress-dialog`
- `integrate install|status|uninstall`

Password inputs:

- `--password <value>`
- `--password-file <path>`
- `--encrypt` for prompting during create workflows.

Non-interactive runs must use explicit password inputs.

## Desktop UI

The desktop app supports create, estimate, open/list, extract, delete selected LPC entries, and test workflows. It supports selected-entry extraction for LPC, ZIP, and managed external archives where entry IDs are available.

Do not assume every WinRAR-style desktop action is implemented. Core LPC deletion is wired; add/rename/comment/lock/repair still primarily live in the CLI.

## Shell Integration

Shell integration is per-user and writes under `HKCU\Software\Classes`. It does not require admin privileges.

It registers:

- `.lpc` association to `Laplace.Archive`.
- A branded `Laplace` archive submenu.
- Create/estimate actions for files, folders, and folder background.

The integration intentionally uses Explorer single-item context actions. True multi-select batching would require a native COM shell extension.

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
  -Version 1.5.0 `
  -MsixVersion 1.5.0.0 `
  -SelfContained
```

Installer build requires Inno Setup 6. MSIX build requires Windows SDK tooling.

## Documentation Rules

Keep documentation precise:

- Do not claim Laplace is generally better than RAR without benchmarks.
- Do not claim ZPAQ or BSC are built in; they are optional external-command adapters.
- Do not claim `LZMA_MAX` is true LZMA until the compressor implementation changes.
- Do not claim metadata encryption, recovery records, multi-volume LPC, solid LPC, or SFX are implemented until the reserved paths are replaced with real behavior.
- Keep LPC and non-LPC behavior separate.
- Mention that incompressible LPC blocks fall back to `RAW`.
