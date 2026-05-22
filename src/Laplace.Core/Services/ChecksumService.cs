using System.IO.Hashing;
using System.Security.Cryptography;

namespace Laplace.Core.Services;

public static class ChecksumService
{
    public static uint ComputeCrc32C(ReadOnlySpan<byte> data) => Crc32C.HashToUInt32(data);

    public static byte[] ComputeSha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }
}
