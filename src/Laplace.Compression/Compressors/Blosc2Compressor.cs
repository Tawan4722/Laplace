using Blosc2.PInvoke;
using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using System.Runtime.InteropServices;

namespace Laplace.Compression.Compressors;

public sealed class Blosc2Compressor : IBlockCompressor
{
    private const int ShuffleFilter = 1;
    private const int TypeSize = 8;
    private const int OutputOverheadBytes = 256;

    private static readonly object InitLock = new();
    private static bool _initialized;

    public CompressionMethod Method => CompressionMethod.Blosc2;
    public int Level => 5;

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        EnsureInitialized();
        var source = data.ToArray();
        var maxOutputSize = checked(source.Length + OutputOverheadBytes);
        var destination = new byte[maxOutputSize];

        var sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        var destinationHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            var compressedSize = Blosc.blosc2_compress(
                Level,
                ShuffleFilter,
                TypeSize,
                sourceHandle.AddrOfPinnedObject(),
                source.Length,
                destinationHandle.AddrOfPinnedObject(),
                destination.Length);

            if (compressedSize <= 0)
            {
                throw new InvalidOperationException($"Blosc2 compression failed with code {compressedSize}.");
            }

            return destination[..compressedSize];
        }
        finally
        {
            sourceHandle.Free();
            destinationHandle.Free();
        }
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        EnsureInitialized();
        if (expectedDecompressedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDecompressedSize));
        }

        var source = data.ToArray();
        var destination = new byte[expectedDecompressedSize];
        var sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        var destinationHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            var decompressedSize = Blosc.blosc2_decompress(
                sourceHandle.AddrOfPinnedObject(),
                source.Length,
                destinationHandle.AddrOfPinnedObject(),
                destination.Length);

            if (decompressedSize != expectedDecompressedSize)
            {
                throw new InvalidDataException($"Blosc2 decompressed {decompressedSize} bytes; expected {expectedDecompressedSize}.");
            }

            return destination;
        }
        finally
        {
            sourceHandle.Free();
            destinationHandle.Free();
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            Blosc.blosc2_init();
            _initialized = true;
        }
    }
}
