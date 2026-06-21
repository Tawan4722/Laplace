using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Laplace.Compression.Compressors;

public sealed class ZstdCompressor : IBlockCompressor
{
    private readonly int? _windowLog;
    private readonly bool _enableLongDistanceMatching;

    public ZstdCompressor(
        CompressionMethod method,
        int level,
        int? windowLog = null,
        bool enableLongDistanceMatching = false)
    {
        if (windowLog is < 10 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(windowLog), "Zstd window log must be between 10 and 31.");
        }

        Method = method;
        Level = level;
        _windowLog = windowLog;
        _enableLongDistanceMatching = enableLongDistanceMatching;
    }

    public CompressionMethod Method { get; }
    public int Level { get; }

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var compressor = new Compressor(Level);
        if (_windowLog is { } windowLog)
        {
            compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
        }
        if (_enableLongDistanceMatching)
        {
            compressor.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        }

        return compressor.Wrap(data).ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        using var decompressor = new Decompressor();
        decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 31);
        return decompressor.Unwrap(data).ToArray();
    }
}
