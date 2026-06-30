using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using System.Security.Cryptography;

namespace Laplace.Core.Services;

public sealed class ArchiveReader
{
    public ArchiveDocument Read(string archivePath, PasswordContext? password = null)
    {
        using var stream = LpcSfxHelper.OpenArchiveStream(archivePath);
        var header = ArchiveFormatCodec.ReadHeader(stream);
        ArchiveValidator.ValidateHeader(header, stream.Length);

        List<FileEntryRecord> files;
        List<BlockEntryRecord> blocks;
        if (header.IsMetadataEncrypted)
        {
            if (password is null)
            {
                throw new ArchivePasswordRequiredException(archivePath);
            }

            var key = ArchiveEncryption.DeriveKey(password, header);
            try
            {
                stream.Position = header.FileTableOffset;
                var filePayload = ArchiveFormatCodec.ReadEncryptedTable(stream, "file table");
                stream.Position = header.BlockTableOffset;
                var blockPayload = ArchiveFormatCodec.ReadEncryptedTable(stream, "block table");
                byte[] filePlaintext = [];
                byte[] blockPlaintext = [];
                try
                {
                    filePlaintext = ArchiveEncryption.DecryptMetadata(
                        filePayload.Ciphertext,
                        filePayload.Nonce,
                        filePayload.Tag,
                        key,
                        "file table",
                        header);
                    blockPlaintext = ArchiveEncryption.DecryptMetadata(
                        blockPayload.Ciphertext,
                        blockPayload.Nonce,
                        blockPayload.Tag,
                        key,
                        "block table",
                        header);
                    using var fileStream = new MemoryStream(filePlaintext, writable: false);
                    using var blockStream = new MemoryStream(blockPlaintext, writable: false);
                    files = ArchiveFormatCodec.ReadFileEntries(fileStream, header.FileEntryCount, header.FormatVersion);
                    blocks = ArchiveFormatCodec.ReadBlockEntries(blockStream, header.BlockEntryCount, header.FormatVersion);
                }
                catch (CryptographicException)
                {
                    throw new ArchivePasswordException("Invalid password or corrupted encrypted LPC metadata.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(filePlaintext);
                    CryptographicOperations.ZeroMemory(blockPlaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        else
        {
            stream.Position = header.FileTableOffset;
            files = ArchiveFormatCodec.ReadFileEntries(stream, header.FileEntryCount, header.FormatVersion);
            stream.Position = header.BlockTableOffset;
            blocks = ArchiveFormatCodec.ReadBlockEntries(stream, header.BlockEntryCount, header.FormatVersion);
        }
        ArchiveValidator.ValidateBlockEntries(blocks, stream.Length, header);

        return new ArchiveDocument
        {
            Header = header,
            FileEntries = files,
            BlockEntries = blocks
        };
    }

    public ArchiveHeader ReadHeaderOnly(string archivePath)
    {
        using var stream = LpcSfxHelper.OpenArchiveStream(archivePath);
        var header = ArchiveFormatCodec.ReadHeader(stream);
        ArchiveValidator.ValidateHeader(header, stream.Length);
        return header;
    }

    public static Dictionary<long, List<BlockEntryRecord>> BuildBlockLookup(ArchiveDocument archive)
    {
        if (archive.Header.IsSolid)
        {
            throw new LaplaceArchiveException("Block lookup by owning file is not valid for solid LPC archives.");
        }

        var map = new Dictionary<long, List<BlockEntryRecord>>();
        foreach (var block in archive.BlockEntries)
        {
            if (!map.TryGetValue(block.OwningFileEntryId, out var list))
            {
                list = [];
                map[block.OwningFileEntryId] = list;
            }
            list.Add(block);
        }

        return map;
    }

    public static void ValidateEntryBlockReferences(ArchiveDocument archive)
    {
        var maxBlockIndex = archive.BlockEntries.Count - 1;
        if (archive.Header.IsSolid)
        {
            long previousEnd = 0;
            var orderedFiles = archive.FileEntries.Where(f => !f.IsDirectory)
                .OrderBy(f => f.DataStreamOffset)
                .ThenBy(f => f.EntryId)
                .ToList();

            foreach (var file in orderedFiles)
            {
                if (file.DataStreamOffset < 0)
                {
                    throw new LaplaceArchiveException($"Invalid data stream offset for file entry {file.RelativePath}.");
                }

                if (file.DataStreamOffset < previousEnd)
                {
                    throw new LaplaceArchiveException($"Solid file ranges overlap or are out of order for {file.RelativePath}.");
                }

                if (file.OriginalSize > 0)
                {
                    if (file.FirstBlockIndex < 0 || file.BlockCount <= 0)
                    {
                        throw new LaplaceArchiveException($"Solid file entry {file.RelativePath} does not reference its covering block range.");
                    }

                    var endIndex = file.FirstBlockIndex + file.BlockCount - 1;
                    if (endIndex > maxBlockIndex)
                    {
                        throw new LaplaceArchiveException($"Solid block range out of bounds for file entry {file.RelativePath}.");
                    }
                }

                previousEnd = file.DataStreamOffset + file.OriginalSize;
            }

            return;
        }

        foreach (var file in archive.FileEntries.Where(f => !f.IsDirectory))
        {
            if (file.FirstBlockIndex < 0 && file.BlockCount > 0)
            {
                throw new LaplaceArchiveException($"Invalid first block index for file entry {file.RelativePath}.");
            }

            if (file.BlockCount < 0)
            {
                throw new LaplaceArchiveException($"Negative block count for file entry {file.RelativePath}.");
            }

            if (file.BlockCount == 0)
            {
                continue;
            }

            var endIndex = file.FirstBlockIndex + file.BlockCount - 1;
            if (endIndex > maxBlockIndex)
            {
                throw new LaplaceArchiveException($"Block range out of bounds for file entry {file.RelativePath}.");
            }
        }
    }
}
