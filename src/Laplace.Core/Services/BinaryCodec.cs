using System.Buffers.Binary;
using System.Text;

namespace Laplace.Core.Services;

internal static class BinaryCodec
{
    private const int MaxStringLengthBytes = 16 * 1024 * 1024;

    public static void WriteUtf8String(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    public static string ReadUtf8String(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            throw new InvalidDataException("Negative string length.");
        }
        if (length > MaxStringLengthBytes)
        {
            throw new InvalidDataException($"String length {length} exceeds supported maximum.");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading UTF-8 string.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public static uint ComputeHeaderChecksum(ReadOnlySpan<byte> headerWithoutChecksum)
    {
        return ChecksumService.ComputeCrc32C(headerWithoutChecksum);
    }

}
