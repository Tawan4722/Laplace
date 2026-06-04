using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using SharpCompress.Compressors.LZMA;

namespace Laplace.Compression.Compressors;

public sealed class LzmaCompressor : IBlockCompressor
{
    private const int PropertyLengthBytes = 5;
    private static readonly LzmaEncoderProperties EncoderProperties = new(false, 1 << 24, 128);

    public CompressionMethod Method => CompressionMethod.LzmaMax;
    public int Level => 9;

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var encoder = LzmaStream.Create(EncoderProperties, false, output))
        {
            output.Write(encoder.Properties, 0, encoder.Properties.Length);
            var bytes = data.ToArray();
            encoder.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        if (data.Length < PropertyLengthBytes)
        {
            throw new InvalidDataException("LZMA block is missing coder properties.");
        }

        var bytes = data.ToArray();
        var properties = bytes[..PropertyLengthBytes];
        using var input = new MemoryStream(bytes, PropertyLengthBytes, bytes.Length - PropertyLengthBytes, writable: false);
        using var lzma = LzmaStream.Create(properties, input, input.Length, expectedDecompressedSize, leaveOpen: false);
        using var output = expectedDecompressedSize > 0
            ? new MemoryStream(expectedDecompressedSize)
            : new MemoryStream();
        lzma.CopyTo(output);
        return output.ToArray();
    }
}
