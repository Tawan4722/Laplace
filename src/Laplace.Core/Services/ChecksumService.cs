using System.Security.Cryptography;

namespace Laplace.Core.Services;

public static class ChecksumService
{
    public static uint ComputeCrc32C(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        var offset = 0;
        var len = data.Length;

        while (len - offset >= 8)
        {
            var val = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
            crc = System.Numerics.BitOperations.Crc32C(crc, val);
            offset += 8;
        }

        if (len - offset >= 4)
        {
            var val = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            crc = System.Numerics.BitOperations.Crc32C(crc, val);
            offset += 4;
        }

        if (len - offset >= 2)
        {
            var val = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            crc = System.Numerics.BitOperations.Crc32C(crc, val);
            offset += 2;
        }

        if (len - offset >= 1)
        {
            crc = System.Numerics.BitOperations.Crc32C(crc, data[offset]);
        }

        return ~crc;
    }




    public static byte[] ComputeSha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        return SHA256.HashData(stream);
    }

    private static readonly uint[] Crc32CTable = BuildCrc32CTable();
    internal static uint[] Crc32CTableForRecovery => Crc32CTable;

    private static uint[] BuildCrc32CTable()
    {
        const uint polynomial = 0x82F63B78u; // Reflected CRC32C Castagnoli polynomial.
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1u) != 0 ? (polynomial ^ (c >> 1)) : (c >> 1);
            }

            table[i] = c;
        }

        return table;
    }
}
