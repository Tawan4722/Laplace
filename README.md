# Laplace

Laplace is a Windows-first archive and compression project built around a custom archive format: **`.lpc`**.

- Project: `Laplace`
- Archive extension: `.lpc`
- Magic header: `LPC1`
- Runtime: .NET 8+

Laplace is implemented in C# end-to-end. It is not a thin wrapper around 7-Zip, WinRAR, or OS shell archive commands.

## Highlights

- Custom `.lpc` container with documented binary layout
- Streaming archive writer/reader architecture
- Per-block compression metadata and checksums
- Adaptive compression engine (file type + entropy + sample scoring)
- Compression methods:
  - `RAW`
  - `LZ4_FAST`
  - `ZSTD_FAST`
  - `ZSTD_BALANCED`
  - `ZSTD_HIGH`
  - `DEFLATE_FALLBACK`
  - `LZMA_MAX` method ID reserved (currently mapped to high-compression profile)
- Integrity and validation:
  - Header checksum
  - Per-block CRC32C
  - Per-file SHA-256
  - Safe extraction path validation
- CLI commands:
  - `compress`, `extract`, `list`, `info`, `test`, `benchmark`
  - Shell helpers (`open`, `extract-here`, `extract-to-folder`, `extract-to-named-folder`)
- Per-user Windows shell integration manager (`integrate install|status|uninstall`)

## Quick Start

### Prerequisites

- Windows
- .NET SDK 8.0+

Optional for packaging:

- Inno Setup 6 (for `.exe` installer)
- Windows SDK (`makeappx.exe`, `signtool.exe`) for MSIX

### One-command setup

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

Or from Command Prompt:

```bat
setup.cmd
```

`setup.ps1` will:

- Validate `.NET SDK` version
- Run `dotnet restore`
- Run `dotnet build`
- Run `dotnet test`
- Report optional installer/MSIX tool availability

Optional flags:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1 -Configuration Debug -SkipTests
```

## CLI Usage

`laplace` is produced by `src/Laplace.Cli`.

### Compress

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- `
  compress <input_path...> <output.lpc> `
  --mode fast|balanced|maximum|auto `
  --block-size 4M|8M|16M|32M|64M `
  --solid on|off|auto `
  --threads <number> `
  --verify
```

Examples:

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- compress .\folder .\build.lpc --mode balanced --block-size 8M --verify
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- compress .\src .\docs .\project.lpc --mode auto
```

### Extract

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- extract .\build.lpc .\out --overwrite
```

### List / Info / Test

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- list .\build.lpc
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- info .\build.lpc
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- test .\build.lpc
```

### Benchmark

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- benchmark .\folder
```

### Shell helpers

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- open .\build.lpc
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- extract-here .\build.lpc
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- extract-to-named-folder .\build.lpc
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- extract-to-folder .\build.lpc .\any-destination
```

## Packaging

### EXE installer (Inno Setup)

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 -Configuration Release -Runtime win-x64 -Version 0.1.0
```

Output:

- `artifacts\installer\LaplaceSetup.exe`

### MSIX package

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-msix.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 0.1.0.0 `
  -PackageName Laplace.Project `
  -Publisher "CN=LaplaceProject"
```

Output:

- `artifacts\msix\Laplace_<version>_<runtime>.msix`

For full MSIX details, see `docs/MSIX.md`.

## Format and Safety

- Detailed `.lpc` format specification: `docs/LPC_FORMAT.md`
- Adaptive compression behavior: `docs/ADAPTIVE_COMPRESSION.md`
- Shell integration design and operations: `docs/SHELL_INTEGRATION.md`

Security and integrity checks include:

- Path traversal and absolute-path extraction rejection
- Header checksum verification
- Offset/size bounds validation
- Block CRC32C verification before decompression
- Decompressed-size checks for each block
- SHA-256 verification for extracted files

## Repository Layout

```text
Laplace.sln
src/
  Laplace.Core/              # archive format, reader/writer, extractor, validator, adaptive logic
  Laplace.Compression/       # compressor implementations and registry
  Laplace.Cli/               # command-line app
  Laplace.ShellIntegration/  # Windows HKCU file association/context verb registration
tests/
  Laplace.Tests/             # unit and integration tests
docs/
  LPC_FORMAT.md
  ADAPTIVE_COMPRESSION.md
  SHELL_INTEGRATION.md
  MSIX.md
installer/
  build-installer.ps1
  build-msix.ps1
  Laplace.iss
```

## Development

Build manually:

```powershell
dotnet restore
dotnet build -c Release
```

Run tests:

```powershell
dotnet test -c Release
```

## Roadmap

- Expand compression backend tuning and performance pipeline
- Improve many-small-files and solid-block behavior
- Implement WPF GUI archive manager
- Improve installer/uninstaller diagnostics and repair flow

## License

MIT. See `LICENSE`.
