using System.Security.Cryptography;

namespace Laplace.Core.Services;

public static class ChecksumService
{
    public static uint ComputeCrc32C(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            var idx = (crc ^ b) & 0xFF;
            crc = Crc32CTable[idx] ^ (crc >> 8);
        }

        return ~crc;
    }

    public static byte[] ComputeSha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
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
