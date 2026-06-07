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
        using var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, Level))
        {
            if (_windowLog is { } windowLog)
            {
                zstd.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
            }
            if (_enableLongDistanceMatching)
            {
                zstd.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
            }

            zstd.Write(data);
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
