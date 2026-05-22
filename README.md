# Laplace

Laplace is a Windows-first archive and compression project centered around a custom archive container format: **`.lpc`**.

- Project identity: `Laplace`
- Archive extension: `.lpc`
- Magic header: `LPC1`
- Primary stack: .NET 8+ (C#)

Laplace is designed as a serious engineering project, not a shell wrapper around 7-Zip/WinRAR/OS archive commands.  
Compression and extraction are implemented in code with a shared backend architecture.

## Status

Current repository state is a strong backend/CLI baseline with documented archive format and core safety checks.

Implemented now:

- Custom `.lpc` binary container (header + file table + block table + data section)
- Archive writer/reader with streaming block processing
- Per-block compression method metadata
- Adaptive compression decision engine (file type + entropy + repetition + sampled scoring)
- Compression methods wired in code:
  - `RAW`
  - `LZ4_FAST`
  - `ZSTD_FAST`
  - `ZSTD_BALANCED`
  - `ZSTD_HIGH`
  - `DEFLATE_FALLBACK`
  - `LZMA_MAX` method ID preserved (currently mapped to high-compression backend profile)
- Integrity:
  - header checksum
  - per-block CRC32C
  - per-file SHA-256
- Safe extraction path validation against traversal/absolute paths
- CLI commands:
  - `compress`
  - `extract`
  - `list`
  - `info`
  - `test`
  - `benchmark`
  - shell helpers (`open`, `extract-here`, `extract-to-folder`, `extract-to-named-folder`)
- Per-user Windows shell integration manager through CLI (`integrate install|status|uninstall`)
- Unit/integration tests for round-trip, security, corruption detection, and multi-input behavior

Planned (next phases):

- Full Windows WPF GUI archive manager
- Native drag-out extraction UX
- Installer/uninstaller package (file association/context menu options in installer UI)
- Repair mode and portable mode
- Broader compressor backend set and deeper performance tuning

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
```

## Build Requirements

- Windows (primary target)
- .NET SDK 8.0 or newer

```powershell
dotnet restore
dotnet build -c Release
```

Run tests:

```powershell
dotnet test -c Release
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

### List

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- list .\build.lpc
```

### Info

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- info .\build.lpc
```

### Test Integrity

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- test .\build.lpc
```

### Benchmark

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- benchmark .\folder
```

### Shell Helper Commands

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- open .\build.lpc
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- extract-here .\build.lpc
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- extract-to-named-folder .\build.lpc
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- extract-to-folder .\build.lpc .\any-destination
```

## `.lpc` Format Summary

Detailed specification: [`docs/LPC_FORMAT.md`](docs/LPC_FORMAT.md)

High-level structure:

1. Global header (`LPC1`, version, offsets, counts, metadata)
2. Data section (sequential block payloads, compressed or raw)
3. File entry table (path, timestamps, sizes, checksums, block references)
4. Block table (method, level, offsets, per-block checksum, flags)

Design principles:

- 64-bit offsets/sizes for large-file support
- list/archive metadata readable without full decompression
- per-block method and checksum for robust validation
- forward-compatible method IDs and version field

## Adaptive Compression Strategy

Detailed behavior: [`docs/ADAPTIVE_COMPRESSION.md`](docs/ADAPTIVE_COMPRESSION.md)

Current adaptive engine evaluates:

- extension and signature hints
- file category classification
- entropy estimate
- repetition ratio
- compressibility estimate
- user-selected mode

Selection flow:

1. Analyze sampled bytes
2. Build candidate method list by mode/content
3. Trial-compress sample with candidates
4. Score by ratio/speed/memory/file-type affinity
5. Use best candidate for block compression
6. Enforce RAW fallback whenever compressed block is not smaller

This guarantees no blind expansion of incompressible data.

## Security and Integrity

Laplace explicitly validates malicious/malformed archive conditions:

- path traversal detection (`..\`, `../`, rooted paths)
- extraction bounded to selected destination root
- header checksum verification
- offset/size bounds validation
- block CRC32C verification before decompression
- decompressed size checks against recorded block size
- file SHA-256 validation after reconstruction

Failure behavior:

- reject invalid archive structures safely
- return clear errors instead of undefined behavior
- do not execute extracted files automatically

## Shell Integration (Current)

Detailed guide: [`docs/SHELL_INTEGRATION.md`](docs/SHELL_INTEGRATION.md)

Per-user registration commands:

```powershell
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- integrate install
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- integrate status
dotnet run --project .\src\Laplace.Cli\Laplace.Cli.csproj -- integrate uninstall
```

Scope:

- `HKCU\Software\Classes` only (no admin required)
- cleanly removable

## Testing

Current tests cover:

- file compression/extraction round-trip
- many tiny files handling
- multi-input compression
- path traversal/absolute path extraction rejection
- corruption detection during archive test flow
- RAW fallback behavior on incompressible-like data

Run:

```powershell
dotnet test
```

## Known Limitations

- GUI is not implemented yet in this branch.
- Installer/uninstaller package is not included yet.
- `LZMA_MAX` method ID is present in format/engine but currently backed by high-compression profile rather than a dedicated LZMA backend implementation.
- `compress-dialog` CLI verb is currently a placeholder until GUI create-archive dialog exists.

## Roadmap

### Phase 2

- Expand compressor backends and tuning depth
- Multithreaded block pipeline improvements
- Solid block grouping for many-small-files optimization
- richer benchmarking output and reporting artifact

### Phase 3

- WPF GUI (`Laplace.GUI`)
- archive browsing, sorting, search, selected extraction, archive info UI
- responsive progress and cancel UX

### Phase 4

- drag-out extraction UX
- recent archives and settings pages

### Phase 5

- deeper shell integration polish
- integration diagnostics/repair actions

### Phase 6

- installer/uninstaller (Windows Apps integration)
- clean uninstall/full-clean options
- optional portable mode behavior

### Phase 7

- expanded test matrix
- docs polish
- release hardening and performance profiling

## Contributing

1. Fork the repository
2. Create a feature branch
3. Implement changes with tests
4. Run:
   - `dotnet build`
   - `dotnet test`
5. Open a pull request with:
   - behavior summary
   - test evidence
   - backward-compatibility notes for `.lpc` format (if applicable)

## License

No license file is currently included in this repository state.  
Add a `LICENSE` file before publishing production releases.
