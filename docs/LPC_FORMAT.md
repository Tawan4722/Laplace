# Laplace `.lpc` Format (`LPC1`)

## Overview

An `.lpc` archive is:

1. Global header
2. Data section (raw/compressed blocks, sequential)
3. File entry table
4. Block table

All offsets and sizes use 64-bit fields where relevant.

## Header Layout

| Field | Type | Notes |
|---|---|---|
| Magic | 4 bytes ASCII | `LPC1` |
| FormatVersion | `UInt16` | `1` unencrypted, `2` supports encrypted payload blocks |
| ArchiveFlags | `UInt16` | bit `1` = encrypted payload blocks |
| CreatedUnixMilliseconds | `Int64` | UTC timestamp |
| CreatorVersion | `UInt32` | Laplace writer version |
| DefaultBlockSize | `UInt32` | default bytes per block |
| FileEntryCount | `Int64` | number of file/directory entries |
| BlockEntryCount | `Int64` | number of block records |
| FileTableOffset | `Int64` | absolute stream offset |
| BlockTableOffset | `Int64` | absolute stream offset |
| DataSectionOffset | `Int64` | absolute stream offset |
| Comment | UTF-8 string | length-prefixed `Int32` + bytes |
| EncryptionAlgorithmId | `Byte` | LPCv2 only; `1` = AES-256-GCM |
| KeyDerivationIterations | `Int32` | LPCv2 only; PBKDF2-HMAC-SHA256 iterations. Current writers default to 600,000 and readers accept 210,000 through 5,000,000. |
| EncryptionSalt | bytes | LPCv2 only; length-prefixed `Int32` + bytes. Current writers generate 32-byte salts; readers require at least 16 bytes. |
| HeaderChecksumCrc32C | `UInt32` | CRC32C over header bytes excluding this field |

LPCv2 encryption protects block payload bytes only. File names, sizes, timestamps, and table metadata remain readable so `list` and `info` can work without decrypting file contents.

## File Entry Table

Each file/folder record stores:

- `EntryId` (`Int64`)
- `ParentFolderId` (`Int64`)
- `RelativePath` (UTF-8 length-prefixed)
- `OriginalSize` (`Int64`)
- `CompressedSize` (`Int64`)
- `CreatedUnixMilliseconds` (`Int64`)
- `ModifiedUnixMilliseconds` (`Int64`)
- `FileAttributes` (`Int32`)
- `IsDirectory` (`Boolean`)
- `IsSymlink` (`Boolean`)
- `FirstBlockIndex` (`Int64`)
- `BlockCount` (`Int32`)
- `CompressionSummary` (UTF-8 length-prefixed)
- `ChecksumType` (`Byte`)
- `FileChecksumLength` (`Int32`)
- `FileChecksum` (`Byte[]`)
- `OptionalMetadataJson` (UTF-8 length-prefixed)

## Block Table

Each block record stores:

- `BlockId` (`Int64`)
- `OwningFileEntryId` (`Int64`)
- `OriginalBlockSize` (`Int32`)
- `CompressedBlockSize` (`Int32`)
- `CompressionMethod` (`Byte`)
- `CompressionLevel` (`Int32`)
- `DataOffset` (`Int64`)
- `BlockChecksumCrc32C` (`UInt32`)
- `Flags` (`UInt32`)
- `IsRaw` (`Boolean`)
- `EncryptionNonce` (LPCv2 only, length-prefixed)
- `EncryptionTag` (LPCv2 only, length-prefixed)

## Compression Method IDs

- `0 = RAW`
- `1 = LZ4_FAST`
- `2 = ZSTD_FAST`
- `3 = ZSTD_BALANCED`
- `4 = ZSTD_HIGH`
- `5 = LZMA_MAX`
- `6 = DEFLATE_FALLBACK`
- `7 = BLOSC2`
- `8 = ZPAQ`
- `9 = BSC`

## Integrity

- Header CRC32C validates archive metadata framing.
- Block CRC32C validates stored block bytes before decompression.
- File SHA-256 validates reconstructed file content.
- Encrypted LPCv2 blocks use AES-256-GCM authentication before decompression.

## Versioning Policy

- `FormatVersion` increments on incompatible binary changes.
- New optional fields should be appended to preserve parser compatibility for known versions.
