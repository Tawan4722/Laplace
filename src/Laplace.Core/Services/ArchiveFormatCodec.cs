using Laplace.Core.Enums;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using System.Text;

namespace Laplace.Core.Services;

internal static class ArchiveFormatCodec
{
    public static long WriteHeader(Stream stream, ArchiveHeader header)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes(ArchiveHeader.Magic));
            writer.Write(header.FormatVersion);
            writer.Write(header.ArchiveFlags);
            writer.Write(header.CreatedUnixMilliseconds);
            writer.Write(header.CreatorVersion);
            writer.Write(header.DefaultBlockSize);
            writer.Write(header.FileEntryCount);
            writer.Write(header.BlockEntryCount);
            writer.Write(header.FileTableOffset);
            writer.Write(header.BlockTableOffset);
            writer.Write(header.DataSectionOffset);
            BinaryCodec.WriteUtf8String(writer, header.Comment);
            if (header.FormatVersion >= 2)
            {
                writer.Write(header.EncryptionAlgorithmId);
                writer.Write(header.KeyDerivationIterations);
                writer.Write(header.EncryptionSalt.Length);
                writer.Write(header.EncryptionSalt);
            }
        }

        var data = ms.ToArray();
        header.HeaderChecksumCrc32C = BinaryCodec.ComputeHeaderChecksum(data);

        using var finalWriter = new BinaryWriter(stream, Encoding.UTF8, true);
        finalWriter.Write(data);
        finalWriter.Write(header.HeaderChecksumCrc32C);
        finalWriter.Flush();
        return data.Length + sizeof(uint);
    }

    public static ArchiveHeader ReadHeader(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var startPosition = stream.Position;
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || Encoding.ASCII.GetString(magic) != ArchiveHeader.Magic)
        {
            throw new LaplaceArchiveException("Invalid LPC magic header.");
        }

        var formatVersion = reader.ReadUInt16();
        var archiveFlags = reader.ReadUInt16();
        var createdUnix = reader.ReadInt64();
        var creatorVersion = reader.ReadUInt32();
        var defaultBlockSize = reader.ReadUInt32();
        var fileEntryCount = reader.ReadInt64();
        var blockEntryCount = reader.ReadInt64();
        var fileTableOffset = reader.ReadInt64();
        var blockTableOffset = reader.ReadInt64();
        var dataSectionOffset = reader.ReadInt64();
        var comment = BinaryCodec.ReadUtf8String(reader);
        byte encryptionAlgorithmId = 0;
        var keyDerivationIterations = 0;
        byte[] encryptionSalt = [];
        if (formatVersion >= 2)
        {
            encryptionAlgorithmId = reader.ReadByte();
            keyDerivationIterations = reader.ReadInt32();
            var saltLength = reader.ReadInt32();
            if (saltLength < 0 || saltLength > 1024)
            {
                throw new LaplaceArchiveException("Invalid encryption salt length.");
            }

            encryptionSalt = reader.ReadBytes(saltLength);
            if (encryptionSalt.Length != saltLength)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading encryption salt.");
            }
        }

        var checksumPosition = stream.Position;
        var checksum = reader.ReadUInt32();

        var headerLengthWithoutChecksum = checksumPosition - startPosition;
        stream.Position = startPosition;
        var headerBytes = new byte[headerLengthWithoutChecksum];
        var read = stream.Read(headerBytes, 0, headerBytes.Length);
        if (read != headerBytes.Length)
        {
            throw new EndOfStreamException("Could not read complete archive header.");
        }

        var computed = BinaryCodec.ComputeHeaderChecksum(headerBytes);
        if (computed != checksum)
        {
            throw new LaplaceArchiveException("Header checksum mismatch.");
        }

        return new ArchiveHeader
        {
            FormatVersion = formatVersion,
            ArchiveFlags = archiveFlags,
            CreatedUnixMilliseconds = createdUnix,
            CreatorVersion = creatorVersion,
            DefaultBlockSize = defaultBlockSize,
            FileEntryCount = fileEntryCount,
            BlockEntryCount = blockEntryCount,
            FileTableOffset = fileTableOffset,
            BlockTableOffset = blockTableOffset,
            DataSectionOffset = dataSectionOffset,
            Comment = comment,
            HeaderChecksumCrc32C = checksum,
            EncryptionAlgorithmId = encryptionAlgorithmId,
            KeyDerivationIterations = keyDerivationIterations,
            EncryptionSalt = encryptionSalt
        };
    }

    public static void WriteFileEntries(Stream stream, IReadOnlyList<FileEntryRecord> entries)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        foreach (var entry in entries)
        {
            writer.Write(entry.EntryId);
            writer.Write(entry.ParentFolderId);
            BinaryCodec.WriteUtf8String(writer, entry.RelativePath);
            writer.Write(entry.OriginalSize);
            writer.Write(entry.CompressedSize);
            writer.Write(entry.CreatedUnixMilliseconds);
            writer.Write(entry.ModifiedUnixMilliseconds);
            writer.Write(entry.FileAttributes);
            writer.Write(entry.IsDirectory);
            writer.Write(entry.IsSymlink);
            writer.Write(entry.FirstBlockIndex);
            writer.Write(entry.BlockCount);
            BinaryCodec.WriteUtf8String(writer, entry.CompressionSummary);
            writer.Write((byte)entry.ChecksumType);
            writer.Write(entry.FileChecksum.Length);
            writer.Write(entry.FileChecksum);
            BinaryCodec.WriteUtf8String(writer, entry.OptionalMetadataJson);
        }
    }

    public static List<FileEntryRecord> ReadFileEntries(Stream stream, long count)
    {
        var list = new List<FileEntryRecord>((int)Math.Min(count, int.MaxValue));
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        for (long i = 0; i < count; i++)
        {
            var entry = new FileEntryRecord
            {
                EntryId = reader.ReadInt64(),
                ParentFolderId = reader.ReadInt64(),
                RelativePath = BinaryCodec.ReadUtf8String(reader),
                OriginalSize = reader.ReadInt64(),
                CompressedSize = reader.ReadInt64(),
                CreatedUnixMilliseconds = reader.ReadInt64(),
                ModifiedUnixMilliseconds = reader.ReadInt64(),
                FileAttributes = reader.ReadInt32(),
                IsDirectory = reader.ReadBoolean(),
                IsSymlink = reader.ReadBoolean(),
                FirstBlockIndex = reader.ReadInt64(),
                BlockCount = reader.ReadInt32(),
                CompressionSummary = BinaryCodec.ReadUtf8String(reader),
                ChecksumType = (ChecksumType)reader.ReadByte()
            };

            var checksumLength = reader.ReadInt32();
            if (checksumLength < 0)
            {
                throw new LaplaceArchiveException("Invalid checksum length in file entry.");
            }

            var checksum = reader.ReadBytes(checksumLength);
            if (checksum.Length != checksumLength)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading file checksum.");
            }

            entry.FileChecksum = checksum;
            entry.OptionalMetadataJson = BinaryCodec.ReadUtf8String(reader);
            list.Add(entry);
        }

        return list;
    }

    public static void WriteBlockEntries(Stream stream, IReadOnlyList<BlockEntryRecord> blocks, ushort formatVersion = 1)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        foreach (var block in blocks)
        {
            writer.Write(block.BlockId);
            writer.Write(block.OwningFileEntryId);
            writer.Write(block.OriginalBlockSize);
            writer.Write(block.CompressedBlockSize);
            writer.Write((byte)block.CompressionMethod);
            writer.Write(block.CompressionLevel);
            writer.Write(block.DataOffset);
            writer.Write(block.BlockChecksumCrc32C);
            writer.Write(block.Flags);
            writer.Write(block.IsRaw);
            if (formatVersion >= 2)
            {
                writer.Write(block.EncryptionNonce.Length);
                writer.Write(block.EncryptionNonce);
                writer.Write(block.EncryptionTag.Length);
                writer.Write(block.EncryptionTag);
            }
        }
    }

    public static List<BlockEntryRecord> ReadBlockEntries(Stream stream, long count, ushort formatVersion = 1)
    {
        var list = new List<BlockEntryRecord>((int)Math.Min(count, int.MaxValue));
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);

        for (long i = 0; i < count; i++)
        {
            var block = new BlockEntryRecord
            {
                BlockId = reader.ReadInt64(),
                OwningFileEntryId = reader.ReadInt64(),
                OriginalBlockSize = reader.ReadInt32(),
                CompressedBlockSize = reader.ReadInt32(),
                CompressionMethod = (CompressionMethod)reader.ReadByte(),
                CompressionLevel = reader.ReadInt32(),
                DataOffset = reader.ReadInt64(),
                BlockChecksumCrc32C = reader.ReadUInt32(),
                Flags = reader.ReadUInt32(),
                IsRaw = reader.ReadBoolean()
            };

            if (formatVersion >= 2)
            {
                var nonceLength = reader.ReadInt32();
                if (nonceLength < 0 || nonceLength > 1024)
                {
                    throw new LaplaceArchiveException("Invalid encrypted block nonce length.");
                }

                block.EncryptionNonce = reader.ReadBytes(nonceLength);
                if (block.EncryptionNonce.Length != nonceLength)
                {
                    throw new EndOfStreamException("Unexpected end of stream while reading encrypted block nonce.");
                }

                var tagLength = reader.ReadInt32();
                if (tagLength < 0 || tagLength > 1024)
                {
                    throw new LaplaceArchiveException("Invalid encrypted block tag length.");
                }

                block.EncryptionTag = reader.ReadBytes(tagLength);
                if (block.EncryptionTag.Length != tagLength)
                {
                    throw new EndOfStreamException("Unexpected end of stream while reading encrypted block tag.");
                }
            }

            list.Add(block);
        }

        return list;
    }
}
