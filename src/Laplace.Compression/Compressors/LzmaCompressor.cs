using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using SharpCompress.Compressors.LZMA;

namespace Laplace.Compression.Compressors;

public sealed class LzmaCompressor : IBlockCompressor
{
    private const int PropertyLengthBytes = 5;
    private readonly LzmaEncoderProperties _encoderProperties;

    public LzmaCompressor(int dictionarySizeBytes = 1 << 24, int fastBytes = 128)
    {
        if (dictionarySizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dictionarySizeBytes));
        }

        if (fastBytes is < 5 or > 273)
        {
            throw new ArgumentOutOfRangeException(nameof(fastBytes), "LZMA fast bytes must be between 5 and 273.");
        }

        _encoderProperties = new LzmaEncoderProperties(false, dictionarySizeBytes, fastBytes);
    }

    public CompressionMethod Method => CompressionMethod.LzmaMax;
    public int Level => 9;

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var encoder = LzmaStream.Create(_encoderProperties, false, output))
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
