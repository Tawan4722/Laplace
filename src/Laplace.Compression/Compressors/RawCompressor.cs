using Laplace.Core.Abstractions;
using Laplace.Core.Enums;

namespace Laplace.Compression.Compressors;

public sealed class RawCompressor : IBlockCompressor
{
    public CompressionMethod Method => CompressionMethod.Raw;
    public int Level => 0;

    public byte[] Compress(ReadOnlySpan<byte> data) => data.ToArray();

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize) => data.ToArray();
}
