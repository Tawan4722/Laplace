using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using System.IO.Compression;

namespace Laplace.Compression.Compressors;

public sealed class DeflateCompressor : IBlockCompressor
{
    public CompressionMethod Method => CompressionMethod.DeflateFallback;
    public int Level => 6;

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = data.ToArray();
            deflate.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var output = expectedDecompressedSize > 0
            ? new MemoryStream(expectedDecompressedSize)
            : new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
