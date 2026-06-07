# Laplace

![Laplace logo](assets/laplace-logo.png)

Laplace is a Windows-first archive manager with a native `.lpc` format, a command-line interface, a desktop application, and Explorer integration.

[Download the latest release](https://github.com/Tawan4722/Laplace/releases/latest)

## Highlights

- Native block-based `.lpc` archives with adaptive compression.
- AES-256-GCM payload and metadata encryption.
- Argon2id password derivation for new encrypted LPC archives.
- Solid archives, recovery records, integrity testing, and repair.
- ZIP, 7z, RAR, CAB, ISO, tar, gzip, bzip2, xz, Zstandard, and lzip workflows.
- Windows desktop UI with operation progress, cancellation, and a dedicated encrypted-archive unlock screen.
- Per-user Explorer context menus and `.lpc` file association.
- Self-contained Windows installer and MSIX packages.

Laplace implements LPC directly. It is not a wrapper around 7-Zip, WinRAR, or Windows shell commands. Some non-LPC formats intentionally use managed libraries or installed external tools where those formats require them.

## Install

Download `LaplaceSetup.exe` from the [latest GitHub Release](https://github.com/Tawan4722/Laplace/releases/latest).

The installer:

- installs under `%LOCALAPPDATA%\Laplace`
- installs `laplace.exe` and `laplace-gui.exe`
- can create desktop and Start Menu shortcuts
- can register `.lpc` files
- can enable the Explorer context menu

The release also provides:

- `Laplace_<version>_win-x64.msix`
- `SHA256SUMS.txt`

## Desktop App

The desktop app supports:

- creating LPC, ZIP, 7z, and RAR archives
- compression estimates
- opening and listing archive contents
- extracting complete archives or selected entries
- deleting selected LPC entries
- archive integrity testing
- archive information
- encrypted archive unlocking and password retry
- metadata encryption and LPC recovery options
- multi-volume 7z and RAR presets
- ISO extraction to a removable drive without formatting or raw-writing it

Long-running work uses a centered progress screen with the current item, percentage when available, and cancellation. Encrypted archives use a dedicated password dialog with archive context, password visibility control, Caps Lock feedback, validation, and retry after an incorrect password.

Opening an `.lpc` file from Explorer launches the desktop app when integration is enabled.

## Supported Formats

### Create

| Format | Support |
| --- | --- |
| `.lpc` | Native |
| `.zip` | Built in, including AES-256 encryption |
| `.7z` | Built in for standard paths; installed 7-Zip is used for advanced or multi-volume output |
| `.rar` | Requires installed `rar.exe` or `WinRAR.exe` |

### Read, List, Test, and Extract

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

For some non-password external archives, Laplace can fall back to Windows `tar.exe` and its libarchive support when the managed reader cannot complete extraction. Unsupported or unreadable formats fail with an explicit error.

## LPC Format

LPC is Laplace's native archive format.

| Version | Capability |
| --- | --- |
| LPCv1 | Unencrypted archives |
| LPCv2 | AES-256-GCM encrypted payload blocks |
| LPCv3 | Locked archives |
| LPCv4 | Native solid archive layout |
| LPCv5 | Versioned Argon2id and PBKDF2 key derivation |
| LPCv6 | Authenticated encryption for file and block tables |
| LPCv7 | Reed-Solomon recovery records |

LPC archives include:

- header CRC32C
- per-block CRC32C
- per-file SHA-256
- authenticated encrypted payloads
- optional encrypted file names and metadata
- optional recovery data for damaged protected shards

New encrypted archives use Argon2id by default. Older PBKDF2-HMAC-SHA256 archives remain readable.

See [LPC format documentation](docs/LPC_FORMAT.md).

## Compression

Laplace analyzes file hints and sampled data before selecting a compressor per block. It considers entropy, repetition, text and zero-byte ratios, reusable patterns, and trial-compressed sizes. A block is stored as `RAW` whenever compression would make it larger.

### Modes

| Mode | Purpose |
| --- | --- |
| `fast` | Prioritize throughput |
| `balanced` | Default size and speed tradeoff |
| `maximum` | Favor smaller output |
| `intensive` | Test the strongest available candidates |
| `compressed` | Ratio-first profile; can delegate advanced 7z/RAR output to installed tools |
| `extreme` | LPC-only, single-worker, memory-tiered large-block compression |
| `auto` | Select candidates from content analysis |

Extreme mode automatically selects 16-256 MiB blocks with tiered 8-128 MiB Zstd long-distance windows and LZMA dictionaries. It requires at least 256 MiB of available compression memory and does not accept an explicit block size.

Built-in or optional method IDs:

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

`BLOSC2` is built in. `ZPAQ` and `BSC` are optional external-command backends and are registered only when their command templates are configured.

See:

- [Adaptive compression](docs/ADAPTIVE_COMPRESSION.md)
- [Ultra Ratio benchmark](docs/ULTRA_RATIO_BENCHMARK.md)

The published benchmark is a reproducible synthetic comparison, not a claim that Laplace is generally better than 7-Zip or WinRAR. Real-world results depend on the corpus and machine.

## CLI

After installation:

```powershell
laplace <command>
```

From source:

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- <command>
```

Common workflows:

```powershell
# Create an LPC archive
laplace compress .\input .\backup.lpc --mode balanced --verify

# Create an encrypted archive with hidden metadata
laplace compress .\input .\private.lpc --encrypt --hide-names --verify

# Maximum practical LPC ratio
laplace compress .\input .\archive.lpc --mode extreme --verify

# Inspect and test
laplace list .\archive.lpc
laplace info .\archive.lpc
laplace test .\archive.lpc

# Extract
laplace extract .\archive.lpc .\output --overwrite

# Manage LPC contents
laplace add .\archive.lpc .\new-files
laplace freshen .\archive.lpc .\changed-files
laplace delete .\archive.lpc "old/**"
laplace rename .\archive.lpc old.txt new.txt

# Search, compare, and repair
laplace find .\archive.lpc --name "*.txt" --text "needle"
laplace diff .\before.lpc .\after.lpc
laplace repair .\damaged.lpc
```

Other commands include `estimate`, `comment`, `lock`, `view`, `merge`, `split`, `benchmark`, `open`, `extract-here`, `extract-to-folder`, `extract-dialog`, `iso-to-drive-dialog`, and `integrate`.

### Password Inputs

- `--password <value>`
- `--password-file <path>`
- `--keyfile <path>` for LPC archives
- `--encrypt` for an interactive create prompt

Non-interactive runs must provide explicit password or keyfile input.

### Automation

Major reporting and workflow commands support:

- `--json`
- `--dry-run`
- `--from-file`
- `--quiet`

See [CLI exit codes](docs/CLI_EXIT_CODES.md) and [shell completions](docs/COMPLETIONS.md).

## Explorer Integration

Manage per-user integration with:

```powershell
laplace integrate install
laplace integrate status
laplace integrate uninstall
```

Integration is stored under `HKCU\Software\Classes` and does not require administrator privileges.

Registered actions include:

- open and inspect archives
- extract with options, here, or to a named folder
- test integrity
- find entries
- repair supported archives
- create archives from files, folders, or folder backgrounds
- create a verified Extreme-mode LPC archive through `Ultra Ratio`
- extract ISO contents to a removable drive

See [Explorer integration](docs/SHELL_INTEGRATION.md).

## Security

Extraction rejects:

- absolute archive paths
- path traversal
- alternate data streams
- Windows reserved device names
- control characters
- unsafe trailing spaces or dots
- writes through existing reparse points such as symlinks and junctions

Encrypted LPC payload blocks and metadata use AES-256-GCM authentication. Password confirmation comparisons use fixed-time equality. LPC integrity checks validate framing, offsets, CRC32C, decompressed sizes, SHA-256, and encrypted authentication tags.

## Build From Source

Requirements:

- Windows
- .NET SDK 8.0 or newer

Run the standard setup:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

Or run each step:

```powershell
dotnet restore
dotnet build Laplace.sln -c Release --no-restore
dotnet test Laplace.sln -c Release --no-build
```

## Packaging

### Installer

Requires Inno Setup 6:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 1.10.0 `
  -SelfContained
```

Output: `artifacts\installer\LaplaceSetup.exe`

### MSIX

Requires Windows SDK tools:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-msix.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 1.10.0.0 `
  -PackageName Laplace.Project `
  -Publisher "CN=LaplaceProject" `
  -SelfContained
```

Output: `artifacts\msix\Laplace_1.10.0.0_win-x64.msix`

The CLI, desktop executable, installer, and MSIX package use the same transparent Laplace logo source.

See [MSIX packaging](docs/MSIX.md).

## Release Verification

The complete release path builds and tests the solution, builds installer and MSIX packages, generates checksums, silently installs the generated installer, runs archive smoke tests, verifies fallback extraction, and checks Explorer integration while restoring previous local registration.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-release.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 1.10.0 `
  -MsixVersion 1.10.0.0 `
  -SelfContained
```

Git tags matching `v*` trigger the GitHub Actions release workflow and publish:

- `LaplaceSetup.exe`
- `Laplace_<version>_win-x64.msix`
- `SHA256SUMS.txt`

## Repository Layout

```text
assets/       Logo and Windows icon assets
docs/         Format, CLI, integration, benchmark, and packaging documentation
installer/    Inno Setup and MSIX build scripts
scripts/      Verification, benchmarks, and shell completions
src/          Core, compression, CLI, desktop, and shell integration projects
tests/        Unit, round-trip, safety, and CLI black-box tests
```

## Current Limitations

- Native LPC multi-volume output is not implemented.
- LPC self-extracting archives are not implemented.
- RAR creation requires WinRAR or RAR command-line tools.
- Advanced 7z and multi-volume 7z output require installed 7-Zip tools.
- Optional ZPAQ and BSC methods require configured external commands.

## License

Laplace is licensed under GPLv3. See [LICENSE](LICENSE).
