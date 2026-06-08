using Laplace.Core.Enums;

namespace Laplace.Core.Abstractions;

public interface IBlockCompressor
{
    CompressionMethod Method { get; }
    int Level { get; }
    byte[] Compress(ReadOnlySpan<byte> data);
    byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize);
}
