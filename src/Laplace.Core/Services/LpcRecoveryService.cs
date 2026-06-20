using Laplace.Core.Exceptions;
using System.Buffers.Binary;
using System.Text;

namespace Laplace.Core.Services;

public sealed class LpcRecoveryService
{
    private const int ShardSize = 64 * 1024;
    private const int DataShardsPerStripe = 32;
    private const int RecordHeaderSize = 32;
    private const int RecordChecksumSize = 4;
    private const int TrailerSize = 28;
    private const ushort RecoveryVersion = 1;
    private static ReadOnlySpan<byte> RecordMagic => "LPCR"u8;
    private static ReadOnlySpan<byte> TrailerMagic => "LPCT"u8;

    public static long CalculateRecordLength(long protectedLength, int recoveryPercent)
    {
        ValidateRecoveryPercent(recoveryPercent);
        var stripeCapacity = (long)ShardSize * DataShardsPerStripe;
        var stripeCount = checked((int)((protectedLength + stripeCapacity - 1) / stripeCapacity));
        long length = RecordHeaderSize + RecordChecksumSize + TrailerSize;
        var remaining = protectedLength;
        for (var stripe = 0; stripe < stripeCount; stripe++)
        {
            var stripeLength = Math.Min(remaining, stripeCapacity);
            var dataShardCount = checked((int)((stripeLength + ShardSize - 1) / ShardSize));
            var parityShardCount = GetParityShardCount(dataShardCount, recoveryPercent);
            length = checked(length + 12L + (dataShardCount * 4L) + (parityShardCount * 4L) + ((long)parityShardCount * ShardSize));
            remaining -= stripeLength;
        }

        return length;
    }

    public static async Task WriteRecordAsync(
        Stream archiveStream,
        long protectedLength,
        int recoveryPercent,
        CancellationToken cancellationToken = default)
    {
        ValidateRecoveryPercent(recoveryPercent);
        var recordOffset = protectedLength;
        var stripeCapacity = (long)ShardSize * DataShardsPerStripe;
        var stripeCount = checked((int)((protectedLength + stripeCapacity - 1) / stripeCapacity));
        var checksum = new Crc32CAccumulator();
        archiveStream.Position = recordOffset;

        await WriteTrackedAsync(archiveStream, RecordMagic.ToArray(), checksum, cancellationToken).ConfigureAwait(false);
        await WriteTrackedAsync(archiveStream, GetBytes(RecoveryVersion), checksum, cancellationToken).ConfigureAwait(false);
        await WriteTrackedAsync(archiveStream, GetBytes((ushort)0), checksum, cancellationToken).ConfigureAwait(false);
        await WriteTrackedAsync(archiveStream, GetBytes(protectedLength), checksum, cancellationToken).ConfigureAwait(false);
        await WriteTrackedAsync(archiveStream, GetBytes(ShardSize), checksum, cancellationToken).ConfigureAwait(false);
        await WriteTrackedAsync(archiveStream, GetBytes(DataShardsPerStripe), checksum, cancellationToken).ConfigureAwait(false);
        await WriteTrackedAsync(archiveStream, GetBytes(recoveryPercent), checksum, cancellationToken).ConfigureAwait(false);
        await WriteTrackedAsync(archiveStream, GetBytes(stripeCount), checksum, cancellationToken).ConfigureAwait(false);

        var remaining = protectedLength;
        var sourceOffset = 0L;
        for (var stripe = 0; stripe < stripeCount; stripe++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stripeLength = Math.Min(remaining, stripeCapacity);
            var dataShardCount = checked((int)((stripeLength + ShardSize - 1) / ShardSize));
            var parityShardCount = GetParityShardCount(dataShardCount, recoveryPercent);
            var lastShardLength = checked((int)(stripeLength - ((long)(dataShardCount - 1) * ShardSize)));
            var dataShards = new byte[dataShardCount][];
            var dataCrcs = new uint[dataShardCount];

            for (var i = 0; i < dataShardCount; i++)
            {
                var shard = new byte[ShardSize];
                var bytesToRead = i == dataShardCount - 1 ? lastShardLength : ShardSize;
                archiveStream.Position = sourceOffset + ((long)i * ShardSize);
                await archiveStream.ReadExactlyAsync(shard.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
                dataShards[i] = shard;
                dataCrcs[i] = ChecksumService.ComputeCrc32C(shard.AsSpan(0, bytesToRead));
            }

            var parityShards = ReedSolomonCodec.Encode(dataShards, parityShardCount, ShardSize);
            var parityCrcs = parityShards.Select(shard => ChecksumService.ComputeCrc32C(shard)).ToArray();
            archiveStream.Position = recordOffset + checksum.BytesProcessed;
            await WriteTrackedAsync(archiveStream, GetBytes(dataShardCount), checksum, cancellationToken).ConfigureAwait(false);
            await WriteTrackedAsync(archiveStream, GetBytes(parityShardCount), checksum, cancellationToken).ConfigureAwait(false);
            await WriteTrackedAsync(archiveStream, GetBytes(lastShardLength), checksum, cancellationToken).ConfigureAwait(false);
            foreach (var crc in dataCrcs)
            {
                await WriteTrackedAsync(archiveStream, GetBytes(crc), checksum, cancellationToken).ConfigureAwait(false);
            }
            foreach (var crc in parityCrcs)
            {
                await WriteTrackedAsync(archiveStream, GetBytes(crc), checksum, cancellationToken).ConfigureAwait(false);
            }
            foreach (var parity in parityShards)
            {
                await WriteTrackedAsync(archiveStream, parity, checksum, cancellationToken).ConfigureAwait(false);
            }

            sourceOffset += stripeLength;
            remaining -= stripeLength;
        }

        archiveStream.Position = recordOffset + checksum.BytesProcessed;
        await archiveStream.WriteAsync(GetBytes(checksum.Value), cancellationToken).ConfigureAwait(false);
        var trailerOffset = archiveStream.Position;
        using var trailer = new MemoryStream(TrailerSize);
        using (var writer = new BinaryWriter(trailer, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(TrailerMagic);
            writer.Write(RecoveryVersion);
            writer.Write((ushort)0);
            writer.Write(recordOffset);
            writer.Write(CalculateRecordLength(protectedLength, recoveryPercent));
            writer.Flush();
        }
        var trailerBytes = trailer.ToArray();
        await archiveStream.WriteAsync(trailerBytes, cancellationToken).ConfigureAwait(false);
        await archiveStream.WriteAsync(GetBytes(ChecksumService.ComputeCrc32C(trailerBytes)), cancellationToken).ConfigureAwait(false);

        var expectedEnd = checked(recordOffset + CalculateRecordLength(protectedLength, recoveryPercent));
        if (archiveStream.Position != expectedEnd || trailerOffset + TrailerSize != expectedEnd)
        {
            throw new InvalidDataException("Recovery record length calculation mismatch.");
        }
    }

    public async Task<int> RepairAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(archivePath);
        var resolvedPath = fullPath;
        if (!File.Exists(resolvedPath))
        {
            if (MultiVolumeStream.IsMultiVolumeFirstFile(fullPath, out var firstVol))
            {
                resolvedPath = firstVol;
            }
            else
            {
                throw new FileNotFoundException("Archive not found.", fullPath);
            }
        }

        var volPaths = MultiVolumeStream.GetVolumePaths(resolvedPath);
        var directory = Path.GetDirectoryName(resolvedPath)!;
        var tempBase = Path.Combine(directory, $".{Path.GetFileName(resolvedPath)}.{Guid.NewGuid():N}.repair");
        var backupBase = Path.Combine(directory, $".{Path.GetFileName(resolvedPath)}.{Guid.NewGuid():N}.bak");

        var tempVolPaths = new List<string>();
        var backupVolPaths = new List<string>();

        for (int i = 0; i < volPaths.Count; i++)
        {
            var originalVol = volPaths[i];
            var tempVol = MultiVolumeStream.GetVolumePath(tempBase, i + 1);
            var backupVol = MultiVolumeStream.GetVolumePath(backupBase, i + 1);

            File.Copy(originalVol, tempVol, overwrite: false);
            tempVolPaths.Add(tempVol);
            backupVolPaths.Add(backupVol);
        }

        try
        {
            int repairedShards;
            await using (var stream = new MultiVolumeStream(tempVolPaths, isWrite: true))
            {
                repairedShards = await RepairStreamAsync(stream, repair: true, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Rename originals to backups, then temps to originals
            for (int i = 0; i < volPaths.Count; i++)
            {
                File.Move(volPaths[i], backupVolPaths[i]);
                File.Move(tempVolPaths[i], volPaths[i]);
            }

            // Delete backups
            for (int i = 0; i < volPaths.Count; i++)
            {
                File.Delete(backupVolPaths[i]);
            }

            return repairedShards;
        }
        catch
        {
            // Restore from backups if they exist
            for (int i = 0; i < volPaths.Count; i++)
            {
                if (File.Exists(backupVolPaths[i]) && !File.Exists(volPaths[i]))
                {
                    File.Move(backupVolPaths[i], volPaths[i]);
                }
            }
            throw;
        }
        finally
        {
            // Clean up temp files
            for (int i = 0; i < tempVolPaths.Count; i++)
            {
                if (File.Exists(tempVolPaths[i]))
                {
                    File.Delete(tempVolPaths[i]);
                }
            }
            // Clean up backup files
            for (int i = 0; i < backupVolPaths.Count; i++)
            {
                if (File.Exists(backupVolPaths[i]))
                {
                    File.Delete(backupVolPaths[i]);
                }
            }
        }
    }

    public async Task ValidateRecordAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        await using var stream = LpcSfxHelper.OpenArchiveStream(archivePath);
        var repairedShards = await RepairStreamAsync(stream, repair: false, cancellationToken).ConfigureAwait(false);
        if (repairedShards != 0)
        {
            throw new LaplaceArchiveException("Archive data requires repair.");
        }
    }

    private static async Task<int> RepairStreamAsync(
        Stream stream,
        bool repair,
        CancellationToken cancellationToken)
    {
        var (recordOffset, recordLength) = await ReadTrailerAsync(stream, cancellationToken).ConfigureAwait(false);
        if (recordOffset <= 0 || recordLength <= RecordHeaderSize + RecordChecksumSize + TrailerSize ||
            recordOffset + recordLength != stream.Length)
        {
            throw new LaplaceArchiveException("Invalid LPC recovery trailer.");
        }

        stream.Position = recordOffset;
        var checksum = new Crc32CAccumulator();
        var magic = await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(RecordMagic))
        {
            throw new LaplaceArchiveException("Invalid LPC recovery record magic.");
        }
        var version = BinaryPrimitives.ReadUInt16LittleEndian(await ReadTrackedAsync(stream, 2, checksum, cancellationToken).ConfigureAwait(false));
        _ = await ReadTrackedAsync(stream, 2, checksum, cancellationToken).ConfigureAwait(false);
        var protectedLength = BinaryPrimitives.ReadInt64LittleEndian(await ReadTrackedAsync(stream, 8, checksum, cancellationToken).ConfigureAwait(false));
        var shardSize = BinaryPrimitives.ReadInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
        var dataShardsPerStripe = BinaryPrimitives.ReadInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
        var recoveryPercent = BinaryPrimitives.ReadInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
        var stripeCount = BinaryPrimitives.ReadInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
        if (version != RecoveryVersion || protectedLength != recordOffset || shardSize != ShardSize ||
            dataShardsPerStripe != DataShardsPerStripe || stripeCount < 1)
        {
            throw new LaplaceArchiveException("Unsupported or invalid LPC recovery record.");
        }
        ValidateRecoveryPercent(recoveryPercent);

        var repairedShards = 0;
        var sourceOffset = 0L;
        for (var stripe = 0; stripe < stripeCount; stripe++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dataShardCount = BinaryPrimitives.ReadInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
            var parityShardCount = BinaryPrimitives.ReadInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
            var lastShardLength = BinaryPrimitives.ReadInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
            if (dataShardCount < 1 || dataShardCount > DataShardsPerStripe ||
                parityShardCount != GetParityShardCount(dataShardCount, recoveryPercent) ||
                lastShardLength < 1 || lastShardLength > ShardSize)
            {
                throw new LaplaceArchiveException("Invalid LPC recovery stripe metadata.");
            }

            var dataCrcs = new uint[dataShardCount];
            var parityCrcs = new uint[parityShardCount];
            for (var i = 0; i < dataShardCount; i++)
            {
                dataCrcs[i] = BinaryPrimitives.ReadUInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
            }
            for (var i = 0; i < parityShardCount; i++)
            {
                parityCrcs[i] = BinaryPrimitives.ReadUInt32LittleEndian(await ReadTrackedAsync(stream, 4, checksum, cancellationToken).ConfigureAwait(false));
            }

            var parityShards = new byte[parityShardCount][];
            for (var i = 0; i < parityShardCount; i++)
            {
                parityShards[i] = await ReadTrackedAsync(stream, ShardSize, checksum, cancellationToken).ConfigureAwait(false);
                if (ChecksumService.ComputeCrc32C(parityShards[i]) != parityCrcs[i])
                {
                    throw new LaplaceArchiveException("LPC recovery parity data is corrupted.");
                }
            }

            var recordPosition = stream.Position;
            var shards = new byte[dataShardCount + parityShardCount][];
            var present = new bool[shards.Length];
            for (var i = 0; i < dataShardCount; i++)
            {
                var bytesToRead = i == dataShardCount - 1 ? lastShardLength : ShardSize;
                var shard = new byte[ShardSize];
                stream.Position = sourceOffset + ((long)i * ShardSize);
                var available = (int)Math.Min(bytesToRead, Math.Max(0, recordOffset - stream.Position));
                if (available > 0)
                {
                    await stream.ReadExactlyAsync(shard.AsMemory(0, available), cancellationToken).ConfigureAwait(false);
                }
                shards[i] = shard;
                present[i] = available == bytesToRead &&
                             ChecksumService.ComputeCrc32C(shard.AsSpan(0, bytesToRead)) == dataCrcs[i];
            }
            for (var i = 0; i < parityShardCount; i++)
            {
                shards[dataShardCount + i] = parityShards[i];
                present[dataShardCount + i] = true;
            }

            var missingData = present.Take(dataShardCount).Count(value => !value);
            if (missingData > 0)
            {
                if (!repair)
                {
                    throw new LaplaceArchiveException($"Recovery stripe {stripe} contains {missingData} damaged protected shards.");
                }

                if (missingData > parityShardCount)
                {
                    throw new LaplaceArchiveException(
                        $"Recovery stripe {stripe} has {missingData} damaged shards but only {parityShardCount} parity shards.");
                }

                ReedSolomonCodec.Reconstruct(shards, present, dataShardCount, parityShardCount, ShardSize);
                for (var i = 0; i < dataShardCount; i++)
                {
                    if (present[i])
                    {
                        continue;
                    }

                    var bytesToWrite = i == dataShardCount - 1 ? lastShardLength : ShardSize;
                    if (ChecksumService.ComputeCrc32C(shards[i].AsSpan(0, bytesToWrite)) != dataCrcs[i])
                    {
                        throw new LaplaceArchiveException($"Recovery reconstruction failed CRC validation for stripe {stripe}, shard {i}.");
                    }
                    stream.Position = sourceOffset + ((long)i * ShardSize);
                    await stream.WriteAsync(shards[i].AsMemory(0, bytesToWrite), cancellationToken).ConfigureAwait(false);
                    repairedShards++;
                }
            }

            sourceOffset += ((long)(dataShardCount - 1) * ShardSize) + lastShardLength;
            stream.Position = recordPosition;
        }

        var storedChecksumBytes = new byte[4];
        await stream.ReadExactlyAsync(storedChecksumBytes, cancellationToken).ConfigureAwait(false);
        var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(storedChecksumBytes);
        if (storedChecksum != checksum.Value)
        {
            throw new LaplaceArchiveException("LPC recovery record checksum mismatch.");
        }

        return repairedShards;
    }

    private static async Task<(long RecordOffset, long RecordLength)> ReadTrailerAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Length < TrailerSize)
        {
            throw new LaplaceArchiveException("Archive does not contain an LPC recovery trailer.");
        }

        stream.Position = stream.Length - TrailerSize;
        var trailer = new byte[TrailerSize];
        await stream.ReadExactlyAsync(trailer, cancellationToken).ConfigureAwait(false);
        if (!trailer.AsSpan(0, 4).SequenceEqual(TrailerMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(trailer.AsSpan(4, 2)) != RecoveryVersion)
        {
            throw new LaplaceArchiveException("Archive does not contain a supported LPC recovery record.");
        }

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(24, 4));
        if (ChecksumService.ComputeCrc32C(trailer.AsSpan(0, 24)) != expectedChecksum)
        {
            throw new LaplaceArchiveException("LPC recovery trailer checksum mismatch.");
        }

        return (
            BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(8, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(16, 8)));
    }

    private static int GetParityShardCount(int dataShardCount, int recoveryPercent)
        => Math.Clamp((int)Math.Ceiling(dataShardCount * recoveryPercent / 100d), 1, 255 - dataShardCount);

    private static void ValidateRecoveryPercent(int recoveryPercent)
    {
        if (recoveryPercent is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryPercent), "Recovery percentage must be between 1 and 100.");
        }
    }

    private static async Task WriteTrackedAsync(
        Stream stream,
        byte[] bytes,
        Crc32CAccumulator checksum,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        checksum.Append(bytes);
    }

    private static async Task<byte[]> ReadTrackedAsync(
        Stream stream,
        int length,
        Crc32CAccumulator checksum,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        checksum.Append(bytes);
        return bytes;
    }

    private static byte[] GetBytes(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] GetBytes(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] GetBytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] GetBytes(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private sealed class Crc32CAccumulator
    {
        private uint _crc = 0xFFFFFFFFu;

        public long BytesProcessed { get; private set; }
        public uint Value => ~_crc;

        public void Append(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                var idx = (_crc ^ b) & 0xFF;
                _crc = ChecksumService.Crc32CTableForRecovery[idx] ^ (_crc >> 8);
            }
            BytesProcessed += data.Length;
        }
    }
}

internal static class ReedSolomonCodec
{
    public static byte[][] Encode(byte[][] dataShards, int parityShardCount, int shardSize)
    {
        var dataShardCount = dataShards.Length;
        var matrix = BuildMatrix(dataShardCount, parityShardCount);
        var parity = Enumerable.Range(0, parityShardCount).Select(_ => new byte[shardSize]).ToArray();
        for (var p = 0; p < parityShardCount; p++)
        {
            CodeRow(matrix[dataShardCount + p], dataShards, parity[p], shardSize);
        }
        return parity;
    }

    public static void Reconstruct(
        byte[][] shards,
        bool[] present,
        int dataShardCount,
        int parityShardCount,
        int shardSize)
    {
        if (present.Count(value => value) < dataShardCount)
        {
            throw new LaplaceArchiveException("Not enough recovery shards are available.");
        }

        var matrix = BuildMatrix(dataShardCount, parityShardCount);
        var subMatrix = new byte[dataShardCount][];
        var subShards = new byte[dataShardCount][];
        var subRow = 0;
        for (var i = 0; i < shards.Length && subRow < dataShardCount; i++)
        {
            if (!present[i])
            {
                continue;
            }
            subMatrix[subRow] = matrix[i].ToArray();
            subShards[subRow] = shards[i];
            subRow++;
        }

        var dataDecodeMatrix = Invert(subMatrix);
        for (var i = 0; i < dataShardCount; i++)
        {
            if (present[i])
            {
                continue;
            }

            shards[i] = new byte[shardSize];
            CodeRow(dataDecodeMatrix[i], subShards, shards[i], shardSize);
        }
    }

    private static byte[][] BuildMatrix(int dataShards, int parityShards)
    {
        var totalShards = dataShards + parityShards;
        var vandermonde = new byte[totalShards][];
        for (var row = 0; row < totalShards; row++)
        {
            vandermonde[row] = new byte[dataShards];
            for (var col = 0; col < dataShards; col++)
            {
                vandermonde[row][col] = Galois.Pow((byte)row, col);
            }
        }

        var top = vandermonde.Take(dataShards).Select(row => row.ToArray()).ToArray();
        var topInverse = Invert(top);
        return Multiply(vandermonde, topInverse);
    }

    private static void CodeRow(byte[] coefficients, byte[][] inputs, byte[] output, int shardSize)
    {
        Array.Clear(output);
        for (var input = 0; input < inputs.Length; input++)
        {
            var coefficient = coefficients[input];
            if (coefficient == 0)
            {
                continue;
            }
            for (var i = 0; i < shardSize; i++)
            {
                output[i] ^= Galois.Multiply(coefficient, inputs[input][i]);
            }
        }
    }

    private static byte[][] Multiply(byte[][] left, byte[][] right)
    {
        var result = new byte[left.Length][];
        for (var row = 0; row < left.Length; row++)
        {
            result[row] = new byte[right[0].Length];
            for (var col = 0; col < right[0].Length; col++)
            {
                byte value = 0;
                for (var i = 0; i < right.Length; i++)
                {
                    value ^= Galois.Multiply(left[row][i], right[i][col]);
                }
                result[row][col] = value;
            }
        }
        return result;
    }

    private static byte[][] Invert(byte[][] matrix)
    {
        var size = matrix.Length;
        var work = new byte[size][];
        for (var row = 0; row < size; row++)
        {
            work[row] = new byte[size * 2];
            matrix[row].CopyTo(work[row], 0);
            work[row][size + row] = 1;
        }

        for (var col = 0; col < size; col++)
        {
            if (work[col][col] == 0)
            {
                var swap = col + 1;
                while (swap < size && work[swap][col] == 0)
                {
                    swap++;
                }
                if (swap == size)
                {
                    throw new LaplaceArchiveException("Recovery matrix is singular.");
                }
                (work[col], work[swap]) = (work[swap], work[col]);
            }

            var scale = Galois.Divide(1, work[col][col]);
            for (var i = 0; i < size * 2; i++)
            {
                work[col][i] = Galois.Multiply(work[col][i], scale);
            }

            for (var row = 0; row < size; row++)
            {
                if (row == col || work[row][col] == 0)
                {
                    continue;
                }
                var factor = work[row][col];
                for (var i = 0; i < size * 2; i++)
                {
                    work[row][i] ^= Galois.Multiply(factor, work[col][i]);
                }
            }
        }

        return work.Select(row => row[size..]).ToArray();
    }

    private static class Galois
    {
        private static readonly byte[] Exp = BuildExp();
        private static readonly byte[] Log = BuildLog();

        public static byte Multiply(byte a, byte b)
            => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

        public static byte Divide(byte a, byte b)
        {
            if (b == 0)
            {
                throw new DivideByZeroException();
            }
            if (a == 0)
            {
                return 0;
            }
            var difference = Log[a] - Log[b];
            return Exp[difference < 0 ? difference + 255 : difference];
        }

        public static byte Pow(byte value, int power)
        {
            if (power == 0)
            {
                return 1;
            }
            if (value == 0)
            {
                return 0;
            }
            return Exp[(Log[value] * power) % 255];
        }

        private static byte[] BuildExp()
        {
            var exp = new byte[512];
            var value = 1;
            for (var i = 0; i < 255; i++)
            {
                exp[i] = (byte)value;
                value <<= 1;
                if ((value & 0x100) != 0)
                {
                    value ^= 0x11D;
                }
            }
            for (var i = 255; i < exp.Length; i++)
            {
                exp[i] = exp[i - 255];
            }
            return exp;
        }

        private static byte[] BuildLog()
        {
            var log = new byte[256];
            for (var i = 0; i < 255; i++)
            {
                log[Exp[i]] = (byte)i;
            }
            return log;
        }
    }
}
