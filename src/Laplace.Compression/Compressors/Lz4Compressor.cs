using K4os.Compression.LZ4;
using Laplace.Core.Abstractions;
using Laplace.Core.Enums;

namespace Laplace.Compression.Compressors;

public sealed class Lz4Compressor : IBlockCompressor
{
    public CompressionMethod Method => CompressionMethod.Lz4Fast;
    public int Level => 0;

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        return LZ4Pickler.Pickle(data, LZ4Level.L00_FAST);
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        return LZ4Pickler.Unpickle(data);
    }
}
