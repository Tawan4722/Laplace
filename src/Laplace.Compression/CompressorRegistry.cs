using Laplace.Compression.Compressors;
using Laplace.Core.Abstractions;
using Laplace.Core.Enums;

namespace Laplace.Compression;

public sealed class CompressorRegistry : ICompressorRegistry, IConfigurableCompressorRegistry
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
            [CompressionMethod.LzmaMax] = new LzmaCompressor(),
            [CompressionMethod.DeflateFallback] = new DeflateCompressor(),
            [CompressionMethod.Blosc2] = new Blosc2Compressor()
        };

        TryRegisterExternal(CompressionMethod.Zpaq, "LAPLACE_ZPAQ_COMPRESS_COMMAND", "LAPLACE_ZPAQ_DECOMPRESS_COMMAND");
        TryRegisterExternal(CompressionMethod.Bsc, "LAPLACE_BSC_COMPRESS_COMMAND", "LAPLACE_BSC_DECOMPRESS_COMMAND");
    }

    public IBlockCompressor GetCompressor(CompressionMethod method)
    {
        if (_compressors.TryGetValue(method, out var compressor))
        {
            return compressor;
        }

        throw new NotSupportedException($"Compression method {method} is not available in this build.");
    }

    public IBlockCompressor GetLzmaCompressor(int dictionarySizeBytes, int fastBytes)
    {
        return new LzmaCompressor(dictionarySizeBytes, fastBytes);
    }

    private void TryRegisterExternal(CompressionMethod method, string compressEnvironmentVariable, string decompressEnvironmentVariable)
    {
        var compressor = ExternalCommandCompressor.TryCreate(method, compressEnvironmentVariable, decompressEnvironmentVariable);
        if (compressor is not null)
        {
            _compressors[method] = compressor;
        }
    }
}
