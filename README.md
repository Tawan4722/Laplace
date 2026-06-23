# Laplace

![Laplace logo](assets/laplace-logo.png)

Laplace is a Windows-first archive manager with a native `.lpc` format, a command-line interface, a desktop application, and Explorer integration.

[Download the latest release](https://github.com/Tawan4722/Laplace/releases/latest)

## Highlights

- Native block-based `.lpc` archives with adaptive compression.
- **Self-Extracting Archives (SFX)**: Package `.lpc` archives as standalone `.exe` files that extract themselves on click without needing Laplace pre-installed.
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
- creating Windows executable Self-Extracting LPC archives (`.exe`)
- compression estimates
- opening and listing archive contents
- extracting complete archives or selected entries
- deleting selected LPC entries
- archive integrity testing
- archive information
- encrypted archive unlocking and password retry
- metadata encryption and LPC recovery options
- multi-volume LPC, 7z, and RAR presets
- ISO extraction to a removable drive without formatting or raw-writing it

Long-running work uses a centered progress screen with the current item, percentage when available, and cancellation. Encrypted archives use a dedicated password dialog with archive context, password visibility control, Caps Lock feedback, validation, and retry after an incorrect password.

Opening an `.lpc` or LPC-formatted `.exe` (SFX) file from Explorer launches the desktop app when integration is enabled.

## Supported Formats

### Create

| Format | Support |
| --- | --- |
| `.lpc` | Native |
| `.exe` | Self-Extracting native LPC executable |
| `.zip` | Built in, including AES-256 encryption |
| `.7z` | Built in for standard paths; installed 7-Zip is used for advanced or multi-volume output |
| `.rar` | Requires installed `rar.exe` or `WinRAR.exe` |

### Read, List, Test, and Extract

- `.lpc` & `.exe` (Self-Extracting LPC)
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

# Create a self-extracting archive (SFX)
laplace compress .\input .\sfx-backup.exe --mode balanced --verify

# Create an encrypted archive with hidden metadata
laplace compress .\input .\private.lpc --encrypt --hide-names --verify

# Maximum practical LPC ratio
laplace compress .\input .\archive.lpc --mode extreme --verify

# Inspect and test (supports native .lpc and .exe SFX files)
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

### Password Inputs

- `--password <value>`
- `--password-file <path>`
- `--keyfile <path>` for LPC archives
- `--encrypt` for an interactive create prompt

Non-interactive runs must provide explicit password or keyfile input.

### Automation

Major reporting and workflow commands support:

- `--json`: Enables machine-readable output. On progress-reporting commands, it stream JSON lines updating real-time bytes, percentage, and current item.
- `--dry-run`
- `--from-file`
- `--quiet`

See [CLI exit codes](docs/CLI_EXIT_CODES.md) and [shell completions](docs/COMPLETIONS.md).

## CLI Command & Function Reference

Laplace provides a powerful, comprehensive set of CLI commands. Below is the detailed syntax and behavior for each of the available functions:

### 1. `compress`
Create an LPC, ZIP, 7z, or RAR archive from the specified input paths.
*   **Syntax**: `laplace compress <input_path...> [output_archive] [options]`
*   **Key Options**:
    *   `--mode <fast|balanced|maximum|intensive|compressed|extreme|auto>`: Compression policy.
    *   `--block-size <size>`: Explicit block size (e.g. `8M`, `16M`).
    *   `--solid <on|off|auto>`: Configure solid mode.
    *   `--threads <count>`: Number of worker threads.
    *   `--volume-size <size>`: Partition output into volumes.
    *   `--hide-names`: Encrypt entry metadata/names.
    *   --include <glob> / --exclude <glob>: Repeatable inclusion/exclusion patterns.
    *   --dedup: Enable block-level deduplication.
    *   --cdc: Enable Content-Defined Chunking (FastCDC).
    *   --min-chunk <size> / --avg-chunk <size> / --max-chunk <size>: FastCDC chunk boundaries.
    *   --encrypt / --password <value> / --password-file <path> / --keyfile <path>: Security parameters.

### 2. `compress-beside`
Compress a single file or directory and output the archive immediately beside it.
*   **Syntax**: `laplace compress-beside <input_path> [options]`
*   **Key Options**: Accepts same compression, security, and glob filtering options as `compress`.

### 3. `estimate`
Analyze input paths and sample data to estimate compression size, ratio, and recommended policies without writing an archive.
*   **Syntax**: `laplace estimate <input_path...> [options]`
*   **Key Options**: `--mode`, `--block-size`, `--solid`, `--threads`, `--include`, `--exclude`, `--json`.

### 4. `extract`
Extract all or matching contents of an archive to a destination folder.
*   **Syntax**: `laplace extract <input_archive> <output_folder> [options]`
*   **Key Options**:
    *   `--name <glob>`: Match entry paths to extract only selected files.
    *   `--overwrite`: Replace existing files.
    *   `--verify` / `--no-verify`: Validate hashes/CRC before writing.
    *   `--continue-on-error`: Skip files with extraction errors and report failures at the end.
    *   `--password` / `--password-file` / `--keyfile`: Access credentials.

### 5. `list`
Print the file entries, directory structure, sizes, and attributes of an archive.
*   **Syntax**: `laplace list <input_archive> [options]`
*   **Key Options**: `--json`, `--password`, `--password-file`, `--keyfile`.

### 6. `info`
Print the technical header details, format version, flags, block counts, and encryption profiles of an archive.
*   **Syntax**: `laplace info <input_archive> [options]`
*   **Key Options**: `--json`, `--password`, `--password-file`, `--keyfile`.

### 7. `test`
Run full integrity validation checks on all block CRC checksums and file SHA-256 hashes inside an archive.
*   **Syntax**: `laplace test <input_archive> [options]`
*   **Key Options**: `--json`, `--password`, `--password-file`, `--keyfile`.

### 8. `add`
Append new files or directories to an existing LPC archive.
*   **Syntax**: `laplace add <archive> <input_path...> [options]`
*   **Key Options**: `--from-file`, `--mode`, `--json`, `--dry-run`, security options.

### 9. `freshen`
Update files in an existing LPC archive only if their modification dates are newer than the archived copies.
*   **Syntax**: `laplace freshen <archive> <input_path...> [options]`
*   **Key Options**: `--from-file`, `--json`, `--dry-run`, security options.

### 10. `delete`
Remove specified file entries or patterns from an LPC archive.
*   **Syntax**: `laplace delete <archive> <entry_path_or_id_or_glob...> [options]`
*   **Key Options**: `--from-file`, `--json`, `--dry-run`, security options.

### 11. `rename`
Rename a file or folder entry within an LPC archive.
*   **Syntax**: `laplace rename <archive.lpc> <entry_path_or_id> <new_entry_path> [options]`
*   **Key Options**: `--json`, `--dry-run`, security options.

### 12. `comment`
Show, set, append, or clear user comment metadata embedded in an LPC archive.
*   **Syntax**: `laplace comment <archive> <--show |--set <text> |--file <path> |--clear> [options]`
*   **Key Options**: `--json`, `--dry-run`, security options.

### 13. `lock`
Lock an LPC archive to permanently freeze its state and prevent any subsequent add, delete, or rename modifications.
*   **Syntax**: `laplace lock <archive> [options]`
*   **Key Options**: `--json`, `--dry-run`, security options.

### 14. `find`
Search for file entries inside an LPC archive filtering by name glob and/or matching string content.
*   **Syntax**: `laplace find <archive> [--name <glob>] [--text <value>] [options]`
*   **Key Options**: `--json`, security options.

### 15. `diff`
Analyze two archives and list all additions, removals, and modifications between them.
*   **Syntax**: `laplace diff <archive_a> <archive_b> [options]`
*   **Key Options**: `--json`, security options.

### 16. `merge`
Consolidate multiple input archives into a single, unified output archive.
*   **Syntax**: `laplace merge <output_archive> <input_archive...> [options]`
*   **Key Options**: `--from-file`, `--mode`, `--json`, `--dry-run`, security options.

### 17. `split`
Split an archive into smaller sequentially-named part volumes based on maximum size or target count.
*   **Syntax**: `laplace split <archive> <output_prefix> <--size <val> |--count <N>> [options]`
*   **Key Options**: `--mode`, `--json`, `--dry-run`, security options.

### 18. `view`
Extract and print the binary or text content of a specific archive entry to stdout.
*   **Syntax**: `laplace view <archive.lpc> <entry_path_or_id> [options]`
*   **Key Options**: `--password`, `--password-file`, `--keyfile`.

### 19. `repair`
Repair corrupted LPC archives using built-in Reed-Solomon recovery records, or trigger RAR recovery routines.
*   **Syntax**: `laplace repair <archive.lpc|archive.rar>`

### 20. `benchmark`
Run standardized compression and decompression benchmarks using active encoders to test speed and ratio on local hardware.
*   **Syntax**: `laplace benchmark <input_path> [--json]`

### 21. `open`
Launch the desktop GUI window and open the specified archive.
*   **Syntax**: `laplace open <archive.lpc>`

### 22. `extract-here`
Extract all archive entries to the current working directory.
*   **Syntax**: `laplace extract-here <archive> [options]`

### 23. `extract-to-folder`
Extract archive contents directly to the specified path without opening selection prompts.
*   **Syntax**: `laplace extract-to-folder <archive> <output_folder> [options]`

### 24. `extract-to-named-folder`
Extract archive contents to a subdirectory named after the archive.
*   **Syntax**: `laplace extract-to-named-folder <archive> [options]`

### 25. `extract-dialog`
Launch the desktop GUI extract dialog directly for the specified archive.
*   **Syntax**: `laplace extract-dialog <archive>`

### 26. `iso-to-drive-dialog`
Open the ISO-to-drive dialog in the desktop GUI to parse and dump an ISO image to a target drive.
*   **Syntax**: `laplace iso-to-drive-dialog <image.iso>`

### 27. `integrate`
Install, check status of, or uninstall the shell context menu integration and file registrations for the current user.
*   **Syntax**: `laplace integrate <install|uninstall|status> [--cli-path <path>]`

### 28. `host`
Spin up a local web server displaying a premium web interface to browse and download files from an archive.
*   **Syntax**: `laplace host <input_archive> [options]`
*   **Key Options**:
    *   `--port <port>`: Port to bind the server (default `8080`, falls back to next free port).
    *   `--single-use`: Closes the web server automatically after the first successful download finishes.
    *   `--password <value>`: Archive password for encrypted archives.

> [!TIP]
> **Remote Stream Support**: Commands that read or query archives (`extract`, `list`, `info`, `test`, `host`) support streaming remote LPC archives directly from HTTP/HTTPS URLs using Range requests, without downloading the full archive file.

### Core API, Services & Function Reference

Laplace is modularly built around a set of core C# services. Below is a comprehensive reference of all service classes, their public functions, signatures, and descriptions:

### 1. Laplace.Core Assembly (`Laplace.Core.Services` Namespace)

#### Archive Management & Write Operations
*   **`ArchiveWriter`** ([ArchiveWriter.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveWriter.cs))
    *   `Task<ArchiveWriteResult> WriteAsync(string outputPath, List<string> inputPaths, CreateArchiveOptions options, IProgress<ArchiveOperationProgress>? progress)`: Creates and writes a new LPC archive from input paths, handling threading, compression, and encryption.
    *   `Task CompressAndWritePayloadBlockAsync(Stream targetStream, byte[] rawData, CompressionMethod method, CryptoContext? crypto, ArchiveWriteResult result)`: Compresses raw block payloads and writes them to the output stream.
*   **`ArchiveEstimator`** ([ArchiveEstimator.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveEstimator.cs))
    *   `Task<ArchiveEstimateResult> EstimateAsync(List<string> inputPaths, EstimateOptions options, IProgress<ArchiveOperationProgress>? progress)`: Simulates block packaging and compression to estimate compressed size.
*   **`LpcArchiveMutationService`** ([LpcArchiveMutationService.cs](file:///e:/Laplace/src/Laplace.Core/Services/LpcArchiveMutationService.cs))
    *   `Task AddEntriesAsync(string archivePath, List<string> inputPaths, MutationOptions options, IProgress<ArchiveOperationProgress>? progress)`: Appends new files/directories to an existing LPC archive.
    *   `Task DeleteEntriesAsync(string archivePath, List<string> entryPathsOrGlobs, MutationOptions options, IProgress<ArchiveOperationProgress>? progress)`: Deletes matching file entries from an LPC archive.
    *   `Task RenameEntryAsync(string archivePath, string entryPathOrId, string newEntryPath, MutationOptions options)`: Renames an archive file entry.
    *   `Task UpdateCommentAsync(string archivePath, string? comment, MutationOptions options)`: Modifies or clears user comment metadata.
    *   `Task LockArchiveAsync(string archivePath, MutationOptions options)`: Sets a permanent locked flag on the archive header to prevent further mutation.

#### Extraction & Validation
*   **`ArchiveExtractor`** ([ArchiveExtractor.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveExtractor.cs))
    *   `Task<ArchiveExtractResult> ExtractAsync(string archivePath, string destinationFolder, ExtractArchiveOptions options, IProgress<ArchiveOperationProgress>? progress)`: Decompresses and extracts LPC archives, verifying SHA-256 and decrypting blocks.
*   **`ArchiveTester`** ([ArchiveTester.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveTester.cs))
    *   `Task<ArchiveTestResult> TestAsync(string archivePath, TestArchiveOptions options, IProgress<ArchiveOperationProgress>? progress)`: Performs dry-run extraction, checking all payload CRC32C and file SHA-256 hashes.
*   **`ArchiveReader`** ([ArchiveReader.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveReader.cs))
    *   `LpcHeader ReadHeaderOnly(string archivePath)`: Quick parse of the main LPC metadata header.
    *   `LpcArchive ReadFullArchive(string archivePath, PasswordContext? password)`: Full parse of the file catalog, block layout, and encryption markers.
*   **`ArchiveValidator`** ([ArchiveValidator.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveValidator.cs))
    *   `void ValidateArchive(LpcArchive archive)`: Confirms structural offsets, file boundaries, and integrity signatures.
*   **`ArchiveInfoBuilder`** ([ArchiveInfoBuilder.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveInfoBuilder.cs))
    *   `Task<ArchiveInfo> BuildInfoAsync(string archivePath, PasswordContext? password)`: Builds an information model containing formats, block types, and compression efficiency.

#### Self-Extracting Archives (SFX)
*   **`LpcSfxHelper`** ([LpcSfxHelper.cs](file:///e:/Laplace/src/Laplace.Core/Services/LpcSfxHelper.cs))
    *   `Task CreateSfxArchiveAsync(string stubPath, string payloadArchivePath, string outputSfxPath)`: Combines the Native AOT stub executable with an LPC payload file to build the SFX archive.
    *   `bool IsSfxFile(string filePath)`: Returns true if the executable has an appended Laplace archive payload signature.
    *   `string GetSfxStubPath()`: Resolves the location of the compiled `laplace-sfx-stub.exe`.

#### Security & Cryptography
*   **`ArchiveEncryption`** ([ArchiveEncryption.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveEncryption.cs))
    *   `byte[] DeriveKey(string password, byte[] salt, KeyDerivationInfo info)`: Derives keys using Argon2id or PBKDF2.
    *   `byte[] EncryptBlock(byte[] plaintext, byte[] key, byte[] nonce, out byte[] tag)`: Runs AES-256-GCM authenticated encryption.
    *   `byte[] DecryptBlock(byte[] ciphertext, byte[] key, byte[] nonce, byte[] tag)`: Decrypts and authenticates payload blocks.
*   **`ArchivePasswordPolicy`** ([ArchivePasswordPolicy.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchivePasswordPolicy.cs))
    *   `PasswordStrength Evaluate(string password)`: Analyzes entropy, characters, and common patterns.

#### Error Recovery & Resilience
*   **`LpcRecoveryService`** ([LpcRecoveryService.cs](file:///e:/Laplace/src/Laplace.Core/Services/LpcRecoveryService.cs))
    *   `Task GenerateRecoveryRecords(string archivePath, double redundancyPercent, IProgress<double>? progress)`: Generates Reed-Solomon error correction codes for block arrays.
    *   `Task<bool> RepairArchiveAsync(string archivePath, string outputPath, IProgress<double>? progress)`: Replaces corrupted or missing data segments using recovery records.

#### Content-Defined Chunking & Analysis
*   **`CdcChunkReader`** ([CdcChunkReader.cs](file:///e:/Laplace/src/Laplace.Core/Services/CdcChunkReader.cs))
    *   `IEnumerable<CdcChunk> SegmentStream(Stream stream, CdcOptions options)`: Splits a continuous file stream using FastCDC rolling hash boundaries for deduplication.
*   **`ArchiveFormatDetector`** ([ArchiveFormatDetector.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveFormatDetector.cs))
    *   `ArchiveFormat Detect(string filePath)`: Identifies file signatures (magic bytes) for LPC, ZIP, 7Z, RAR, CAB, ISO, TAR, GZ, and others.

#### Glob, Path, & Helper Utilities
*   **`GlobFilter`** ([GlobFilter.cs](file:///e:/Laplace/src/Laplace.Core/Services/GlobFilter.cs))
    *   `bool Matches(string path, string pattern)`: Matches a path against glob wildcard patterns (e.g. `**/*.txt`).
*   **`ArchivePathHelper`** ([ArchivePathHelper.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchivePathHelper.cs))
    *   `string NormalizePath(string path)`: Standardizes slashes and directory separators.
    *   `bool IsSafePath(string path)`: Checks for directory traversal attacks, reserved names, or alternate streams.
*   **`ArchivePathScanner`** ([ArchivePathScanner.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchivePathScanner.cs))
    *   `List<string> Scan(IEnumerable<string> paths)`: Expands directories recursively, gathering files to process.
*   **`ArchiveVolumePathHelper`** ([ArchiveVolumePathHelper.cs](file:///e:/Laplace/src/Laplace.Core/Services/ArchiveVolumePathHelper.cs))
    *   `List<string> GenerateVolumeNames(string baseName, int count)`: Generates filenames for multi-volume spanned archives.
*   **`ChecksumService`** ([ChecksumService.cs](file:///e:/Laplace/src/Laplace.Core/Services/ChecksumService.cs))
    *   `uint CalculateCRC32C(byte[] data)`: Fast CRC32C computation.
    *   `byte[] CalculateSHA256(Stream stream)`: Standard SHA-256 hashing.

#### Stream & I/O Adaptors
*   **`HttpRangeStream`** ([HttpRangeStream.cs](file:///e:/Laplace/src/Laplace.Core/Services/HttpRangeStream.cs))
    *   `int Read(byte[] buffer, int offset, int count)`: Implements random access over remote HTTP URLs using HTTP Range requests.
*   **`MultiVolumeStream`** ([MultiVolumeStream.cs](file:///e:/Laplace/src/Laplace.Core/Services/MultiVolumeStream.cs))
    *   Allows reading from multi-volume archives sequentially as a single contiguous stream.
*   **`SubStream`** ([SubStream.cs](file:///e:/Laplace/src/Laplace.Core/Services/SubStream.cs))
    *   Exposes a bounded segment of a parent stream as a standalone stream.
*   **`BinaryCodec`** ([BinaryCodec.cs](file:///e:/Laplace/src/Laplace.Core/Services/BinaryCodec.cs))
    *   Encodes and decodes primitive types with endian-safe structures.

#### Third-Party Tool Adaptors & Formats
*   **`UniversalArchiveService`** ([UniversalArchiveService.cs](file:///e:/Laplace/src/Laplace.Core/Services/UniversalArchiveService.cs))
    *   `Task<List<ArchiveEntry>> ListEntriesAsync(string path, PasswordContext? pwd)`: Lists file items in any supported format.
    *   `Task ExtractAsync(string path, string outDir, ExtractArchiveOptions options, IProgress<ArchiveOperationProgress>? progress)`: Universal entry extractor.
    *   `Task<ArchiveTestResult> TestAsync(string path, TestArchiveOptions options)`: Universal entry tester.
*   **`ZipArchiveWriter`** ([ZipArchiveWriter.cs](file:///e:/Laplace/src/Laplace.Core/Services/ZipArchiveWriter.cs)) & **`ZipArchiveHandler`** ([ZipArchiveHandler.cs](file:///e:/Laplace/src/Laplace.Core/Services/ZipArchiveHandler.cs))
    *   Write and read ZIP format natively, supporting AES-256 standard encryption.
*   **`SevenZipArchiveWriter`** ([SevenZipArchiveWriter.cs](file:///e:/Laplace/src/Laplace.Core/Services/SevenZipArchiveWriter.cs)) & **`RarArchiveWriter`** ([RarArchiveWriter.cs](file:///e:/Laplace/src/Laplace.Core/Services/RarArchiveWriter.cs))
    *   Interfaces and command lines to write 7z and RAR archives.
*   **`RarToolCommandService`** ([RarToolCommandService.cs](file:///e:/Laplace/src/Laplace.Core/Services/RarToolCommandService.cs))
    *   Locates and invokes external `rar.exe` or `WinRAR.exe` commands.
*   **`SharpCompressArchiveHandler`** ([SharpCompressArchiveHandler.cs](file:///e:/Laplace/src/Laplace.Core/Services/SharpCompressArchiveHandler.cs))
    *   Exposes extraction and listing logic for CAB, TAR, GZIP, BZIP2, and Zstd.
*   **`WindowsNativeArchiveHandler`** ([WindowsNativeArchiveHandler.cs](file:///e:/Laplace/src/Laplace.Core/Services/WindowsNativeArchiveHandler.cs))
    *   Leverages Windows `tar.exe` / libarchive commands as extraction fallbacks.

---

### 2. Laplace.Compression Assembly (`Laplace.Compression` Namespace)

*   **`CompressorRegistry`** ([CompressorRegistry.cs](file:///e:/Laplace/src/Laplace.Compression/CompressorRegistry.cs))
    *   `ICompressor GetCompressor(CompressionMethod method)`: Retrieves the compressor instance registered for the method.
    *   `void RegisterCompressor(CompressionMethod method, ICompressor compressor)`: Adds a compressor implementation to the registry.
*   **`ICompressor` Interface** (Implemented by raw, LZ4, Zstd, LZMA, Blosc2, and external formats):
    *   `byte[] Compress(byte[] input)`: Compresses a byte block.
    *   `byte[] Decompress(byte[] input, int decompressedLength)`: Decompresses a byte block.

---

### 3. Laplace.ShellIntegration Assembly (`Laplace.ShellIntegration` Namespace)

*   **`ShellIntegrationManager`** ([ShellIntegrationManager.cs](file:///e:/Laplace/src/Laplace.ShellIntegration/ShellIntegrationManager.cs))
    *   `ShellIntegrationStatus GetStatus()`: Checks if context menus and file associations are registered in the Windows Registry (`HKCU\Software\Classes`).
    *   `void Install(string cliPath)`: Creates Registry keys, adding Explorer right-click commands for compressing, extracting, testing, repairing, and viewing files.
    *   `void Uninstall()`: Removes all registered explorer extension keys.

---

### 4. Laplace.SfxStub Assembly (`Laplace.SfxStub` Namespace)

*   **`Program`** ([Program.cs](file:///e:/Laplace/src/Laplace.SfxStub/Program.cs))
    *   `Task<int> Main(string[] args)`: The main entry point of the self-extracting archive (SFX) stub executable. It parses output directory options, checks for encryption, prompts for password input if needed, and initiates extraction.
    *   `string ReadPassword()`: Helper method to securely read character input from the console without echoing.

---

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

**Note:** Ensure your development environment has the necessary workloads for desktop and CLI application development installed.

Run the standard setup:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

Or run each step manually:

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
  -Version 2.1.1 `
  -SelfContained
```

Output: `artifacts\installer\LaplaceSetup.exe`

### MSIX

Requires Windows SDK tools:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-msix.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 2.1.1.0 `
  -PackageName Laplace.Project `
  -Publisher "CN=LaplaceProject" `
  -SelfContained
```

Output: `artifacts\msix\Laplace_2.1.1.0_win-x64.msix`

The CLI, desktop executable, installer, and MSIX package use the same transparent Laplace logo source.

See [MSIX packaging](docs/MSIX.md).

## Release Verification

The complete release path builds and tests the solution, builds installer and MSIX packages, generates checksums, silently installs the generated installer, runs archive smoke tests, verifies fallback extraction, and checks Explorer integration while restoring previous local registration.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-release.ps1 `
  -Configuration Release `
  -Runtime win-x64 `
  -Version 2.1.1 `
  -MsixVersion 2.1.1.0 `
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

- RAR creation requires WinRAR or RAR command-line tools.
- Advanced 7z and multi-volume 7z output require installed 7-Zip tools.
- Optional ZPAQ and BSC methods require configured external commands.

## License

Laplace is licensed under GPLv3. See [LICENSE](LICENSE).
