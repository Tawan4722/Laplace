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
            
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            var buffer = pool.Rent(Math.Min(data.Length, 64 * 1024));
            try
            {
                var remaining = data.Length;
                var offset = 0;
                while (remaining > 0)
                {
                    var chunk = Math.Min(remaining, buffer.Length);
                    data.Slice(offset, chunk).CopyTo(buffer);
                    encoder.Write(buffer, 0, chunk);
                    offset += chunk;
                    remaining -= chunk;
                }
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        if (data.Length < PropertyLengthBytes)
        {
            throw new InvalidDataException("LZMA block is missing coder properties.");
        }

        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var rentedInput = pool.Rent(data.Length);
        data.CopyTo(rentedInput);
        try
        {
            var properties = new byte[PropertyLengthBytes];
            Array.Copy(rentedInput, 0, properties, 0, PropertyLengthBytes);
            
            using var input = new MemoryStream(rentedInput, PropertyLengthBytes, data.Length - PropertyLengthBytes, writable: false);
            using var lzma = LzmaStream.Create(properties, input, input.Length, expectedDecompressedSize, leaveOpen: false);
            
            if (expectedDecompressedSize > 0)
            {
                var decompressed = new byte[expectedDecompressedSize];
                var readBuffer = pool.Rent(Math.Min(expectedDecompressedSize, 64 * 1024));
                try
                {
                    var totalRead = 0;
                    while (totalRead < expectedDecompressedSize)
                    {
                        var remaining = expectedDecompressedSize - totalRead;
                        var toRead = Math.Min(remaining, readBuffer.Length);
                        var read = lzma.Read(readBuffer, 0, toRead);
                        if (read <= 0)
                        {
                            break;
                        }
                        Array.Copy(readBuffer, 0, decompressed, totalRead, read);
                        totalRead += read;
                    }
                    if (totalRead != expectedDecompressedSize)
                    {
                        throw new InvalidDataException($"LZMA decompression output size mismatch. Expected {expectedDecompressedSize}, got {totalRead}.");
                    }
                }
                finally
                {
                    pool.Return(readBuffer);
                }
                return decompressed;
            }
            else
            {
                using var output = new MemoryStream();
                lzma.CopyTo(output);
                return output.ToArray();
            }
        }
        finally
        {
            pool.Return(rentedInput);
        }
    }
}
