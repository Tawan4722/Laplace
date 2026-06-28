using System.Buffers.Binary;
using System.Text;

namespace Laplace.Core.Services;

internal static class BinaryCodec
{
    private const int MaxStringLengthBytes = 16 * 1024 * 1024;

    public static void WriteUtf8String(BinaryWriter writer, string value)
    {
        if (value.Length == 0)
        {
            writer.Write(0);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        writer.Write(byteCount);

        if (byteCount <= 1024)
        {
            Span<byte> buffer = stackalloc byte[1024];
            Encoding.UTF8.GetBytes(value, buffer);
            writer.Write(buffer[..byteCount]);
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes);
        }
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

        if (length == 0)
        {
            return string.Empty;
        }

        if (length <= 1024)
        {
            Span<byte> buffer = stackalloc byte[1024];
            var slice = buffer[..length];
            int totalRead = 0;
            while (totalRead < length)
            {
                var read = reader.Read(slice[totalRead..]);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream while reading UTF-8 string.");
                }
                totalRead += read;
            }
            return Encoding.UTF8.GetString(slice);
        }
        else
        {
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading UTF-8 string.");
            }
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public static uint ComputeHeaderChecksum(ReadOnlySpan<byte> headerWithoutChecksum)
    {
        return ChecksumService.ComputeCrc32C(headerWithoutChecksum);
    }

}
