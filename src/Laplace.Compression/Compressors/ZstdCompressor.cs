using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using ZstdSharp;

namespace Laplace.Compression.Compressors;

public sealed class ZstdCompressor : IBlockCompressor
{
    public ZstdCompressor(CompressionMethod method, int level)
    {
        Method = method;
        Level = level;
    }

    public CompressionMethod Method { get; }
    public int Level { get; }

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, Level))
        {
            var bytes = data.ToArray();
            zstd.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var zstd = new DecompressionStream(input);
        using var output = expectedDecompressedSize > 0
            ? new MemoryStream(expectedDecompressedSize)
            : new MemoryStream();
        zstd.CopyTo(output);
        return output.ToArray();
    }
}
