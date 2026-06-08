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
| FormatVersion | `UInt16` | highest required feature version, currently `1` through `7` |
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
| EncryptionAlgorithmId | `Byte` | version 2+; `1` = AES-256-GCM |
| KeyDerivationAlgorithmId | `Byte` | version 5+; `1` = PBKDF2-HMAC-SHA256, `2` = Argon2id |
| KeyDerivationIterations | `Int32` | PBKDF2 iterations or Argon2id time cost |
| KeyDerivationMemoryKiB | `Int32` | version 5+; Argon2id memory cost |
| KeyDerivationParallelism | `Int32` | version 5+; Argon2id lanes |
| EncryptionSalt | bytes | length-prefixed `Int32` + bytes; current writers generate 32 bytes |
| RecoveryRecordOffset | `Int64` | version 7+; absolute offset of the recovery section |
| RecoveryRecordLength | `Int64` | version 7+; recovery section plus trailer length |
| RecoveryPercent | `Int32` | version 7+; requested parity percentage |
| HeaderChecksumCrc32C | `UInt32` | CRC32C over header bytes excluding this field |

New encrypted archives use Argon2id with a time cost of 3, 64 MiB of memory, and up to 4 lanes by default. Readers retain the implicit PBKDF2 interpretation used by LPCv2-v4. LPCv5 records the KDF explicitly and can identify either algorithm.

Current archive flags:

- bit `1` = encrypted payload blocks
- bit `2` = locked archive
- bit `4` = solid archive layout
- bit `8` = encrypted file and block tables
- bit `16` = Reed-Solomon recovery record

Feature versions:

- LPCv1: base unencrypted layout
- LPCv2: AES-256-GCM payload encryption with implicit PBKDF2-HMAC-SHA256
- LPCv3: locked archive flag
- LPCv4: native solid stream layout
- LPCv5: explicit KDF algorithm and parameters
- LPCv6: encrypted metadata tables
- LPCv7: recovery section and end trailer

`FormatVersion` is the highest version required by the selected feature combination. Multi-volume and SFX output remain reserved.

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
- `DataStreamOffset` (`Int64`, LPCv4 only; uncompressed byte offset within the solid stream)
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
- `OriginalStreamOffset` (`Int64`, LPCv4 only; uncompressed byte offset where this solid block begins)
- `DataOffset` (`Int64`)
- `BlockChecksumCrc32C` (`UInt32`)
- `Flags` (`UInt32`)
- `IsRaw` (`Boolean`)
- `EncryptionNonce` (LPCv2 only, length-prefixed)
- `EncryptionTag` (LPCv2 only, length-prefixed)

For solid LPC archives, `OwningFileEntryId` is set to `-1` and blocks are mapped back to files through `DataStreamOffset`, `FirstBlockIndex`, and `BlockCount`.

Solid block compression may run concurrently. Blocks are still written in ascending `BlockId` and `OriginalStreamOffset` order, so the on-disk layout remains deterministic.

## Encrypted Metadata Tables

When archive flag bit `8` is set, the file and block tables are serialized normally and then encrypted independently with AES-256-GCM. Each table location contains:

- `CiphertextLength` (`Int32`)
- `NonceLength` (`Int32`)
- `Nonce` (`Byte[]`, currently 12 bytes)
- `TagLength` (`Int32`)
- `Tag` (`Byte[]`, currently 16 bytes)
- `Ciphertext` (`Byte[]`)

The authentication data binds the table kind, format version, archive flags, file count, and block count. Listing, information, extraction, testing, and mutation require the archive password because table contents include paths and block offsets.

## Recovery Section

LPCv7 protects every byte before `RecoveryRecordOffset`, including the header, payload blocks, and metadata tables. Data is divided into 64 KiB shards and stripes of at most 32 data shards. Each stripe adds `ceil(dataShardCount * RecoveryPercent / 100)` Reed-Solomon parity shards.

Recovery record header:

- magic `LPCR`
- recovery version (`UInt16`, currently `1`)
- reserved (`UInt16`)
- protected length (`Int64`)
- shard size (`Int32`)
- maximum data shards per stripe (`Int32`)
- recovery percentage (`Int32`)
- stripe count (`Int32`)

Each stripe stores:

- data shard count (`Int32`)
- parity shard count (`Int32`)
- final data shard length (`Int32`)
- CRC32C for each data shard
- CRC32C for each parity shard
- full-size parity shard bytes

The record ends with its CRC32C. A fixed 28-byte `LPCT` trailer stores the recovery version, record offset, record length, and trailer CRC32C. Repair locates this trailer from the end of the file, identifies damaged data shards by CRC32C, and reconstructs up to the available parity count without needing the encryption password.

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
- Encrypted blocks use AES-256-GCM authentication before decompression.
- LPCv6 metadata tables use independent AES-256-GCM nonces and tags.
- LPCv7 recovery records use per-shard CRC32C, parity CRC32C, a record CRC32C, and a trailer CRC32C.

## Versioning Policy

- `FormatVersion` increments on incompatible binary changes.
- New optional fields should be appended to preserve parser compatibility for known versions.
