using Laplace.Compression.Compressors;
using Laplace.Core.Abstractions;
using Laplace.Core.Enums;

namespace Laplace.Compression;

public sealed class CompressorRegistry : ICompressorRegistry
{
    private readonly Dictionary<CompressionMethod, IBlockCompressor> _compressors;

    public CompressorRegistry()
    {
        _compressors = new Dictionary<CompressionMethod, IBlockCompressor>
        {
            [CompressionMethod.Raw] = new RawCompressor(),
            [CompressionMethod.Lz4Fast] = new Lz4Compressor(),
            [CompressionMethod.ZstdFast] = new ZstdCompressor(CompressionMethod.ZstdFast, 1),
            [CompressionMethod.ZstdBalanced] = new ZstdCompressor(CompressionMethod.ZstdBalanced, 6),
            [CompressionMethod.ZstdHigh] = new ZstdCompressor(CompressionMethod.ZstdHigh, 15),
            [CompressionMethod.LzmaMax] = new ZstdCompressor(CompressionMethod.LzmaMax, 19),
            [CompressionMethod.DeflateFallback] = new DeflateCompressor()
        };
    }

    public IBlockCompressor GetCompressor(CompressionMethod method)
    {
        if (_compressors.TryGetValue(method, out var compressor))
        {
            return compressor;
        }

        throw new NotSupportedException($"Compression method {method} is not available in this build.");
    }
}
