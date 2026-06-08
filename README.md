# Laplace

![Laplace logo](assets/laplace-logo.png)

Laplace is a high-performance, Windows-first archive manager featuring its native `.lpc` format, a robust command-line interface, a desktop GUI, and seamless Explorer integration.

[Download Latest Release](https://github.com/Tawan4722/Laplace/releases/latest)

## Table of Contents
- [Highlights](#highlights)
- [Quick Start](#quick-start)
- [Key Features](#key-features)
- [Supported Formats](#supported-formats)
- [Desktop Application](#desktop-application)
- [CLI Reference](#cli-reference)
- [Security](#security)
- [Documentation](#documentation)
- [Development & Contributing](#development--contributing)
- [License](#license)

## Highlights

- **Native `.lpc` Format:** Block-based archives with adaptive, entropy-aware compression.
- **Robust Security:** AES-256-GCM authenticated encryption for payloads and metadata, utilizing Argon2id key derivation.
- **Data Integrity:** Solid archives, Reed-Solomon recovery records, and comprehensive integrity testing.
- **Versatile UI:** Native Windows desktop application with operation progress and a dedicated encrypted-archive unlock screen.
- **Shell Integration:** Per-user Explorer context menus for intuitive archive management.

## Quick Start

Download `LaplaceSetup.exe` from the [latest GitHub Release](https://github.com/Tawan4722/Laplace/releases/latest) to install.

Once installed, you can use Laplace from the command line:

```powershell
# Create an LPC archive
laplace compress .\my-files .\backup.lpc --mode balanced --verify

# Extract an archive
laplace extract .\backup.lpc .\destination
```

## Key Features

Laplace is built for efficiency, implementing its own compression pipeline rather than relying on wrappers.

- **Adaptive Compression:** Analyzes file hints and samples data to select the optimal compressor (LZ4, Zstd, LZMA, Blosc2, etc.) per block.
- **Extreme Mode:** LPC-only, large-block compression with memory-tiered long-distance windows, designed for high-ratio tasks.
- **Flexible Workflow:** Supports ZIP, 7z, RAR, CAB, ISO, tar, gzip, bzip2, xz, Zstandard, and lzip.

## Supported Formats

### Create
| Format | Support |
| --- | --- |
| `.lpc` | Native |
| `.zip` | Built-in (with AES-256) |
| `.7z` | Built-in |
| `.rar` | External tools required |

### Read, List, Test, & Extract
Laplace supports a wide array of formats, falling back to Windows native tools where necessary for non-password external archives.

*Supported formats:* `.lpc`, `.zip`, `.7z`, `.rar`, `.cab`, `.iso`, `.tar`, `.tar.gz`, `.tgz`, `.tar.bz2`, `.tbz2`, `.tar.xz`, `.txz`, `.gz`, `.bz2`, `.xz`, `.zst`, `.lzip`.

## Desktop Application

The desktop GUI provides an intuitive interface for:
- Creating and managing archives (`.lpc`, `.zip`, `.7z`, `.rar`)
- Extracting selected entries
- Integrity testing
- Archive information and metadata inspection

## CLI Reference

After installation, the `laplace` command is available.

```powershell
# Create an encrypted archive with hidden metadata
laplace compress .\input .\private.lpc --encrypt --hide-names --verify

# Inspect archive contents
laplace list .\archive.lpc

# Search and repair
laplace find .\archive.lpc --name "*.txt" --text "needle"
laplace repair .\damaged.lpc
```

For a comprehensive guide, see [CLI Exit Codes](docs/CLI_EXIT_CODES.md).

## Security

Laplace is designed with security as a priority:
- **Path Hardening:** Rejects absolute paths, path traversal, reserved device names, and reparse points.
- **Authenticated Encryption:** Uses AES-256-GCM with authentication tags for payload and metadata blocks.
- **Fixed-time Comparison:** Password checks utilize fixed-time equality to mitigate timing attacks.

## Documentation

Detailed documentation is available in the `docs/` directory:

- [Adaptive Compression](docs/ADAPTIVE_COMPRESSION.md)
- [LPC Format Details](docs/LPC_FORMAT.md)
- [Explorer Integration](docs/SHELL_INTEGRATION.md)
- [Ultra Ratio Benchmark](docs/ULTRA_RATIO_BENCHMARK.md)
- [MSIX Packaging](docs/MSIX.md)

## Development & Contributing

Requirements: Windows and .NET SDK 8.0+.

1. Clone the repository.
2. Run setup: `powershell -ExecutionPolicy Bypass -File .\setup.ps1`
3. Build and test:
   ```powershell
   dotnet build Laplace.sln -c Release
   dotnet test Laplace.sln -c Release
   ```

We welcome contributions! Please see [CHANGELOG.md](CHANGELOG.md) for recent changes and refer to existing tests for best practices.

## License

Laplace is licensed under the **GPLv3**. See [LICENSE](LICENSE) for details.
