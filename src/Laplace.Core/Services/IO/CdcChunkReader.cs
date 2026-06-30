using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Laplace.Core.Services;

public sealed class CdcChunkReader
{
    private readonly Stream _stream;
    private readonly int _minSize;
    private readonly int _avgSize;
    private readonly int _maxSize;
    private readonly byte[] _buffer;
    private int _bufferOffset;
    private readonly uint _mask;
    private bool _isEof;
    private static readonly uint[] GearTable = new uint[256];

    static CdcChunkReader()
    {
        var rand = new Random(4722);
        for (int i = 0; i < 256; i++)
        {
            GearTable[i] = (uint)(rand.NextDouble() * uint.MaxValue);
        }
    }

    public CdcChunkReader(Stream stream, int minSize, int avgSize, int maxSize)
    {
        _stream = stream;
        _minSize = minSize;
        _avgSize = avgSize;
        _maxSize = maxSize;
        _buffer = new byte[maxSize];
        _bufferOffset = 0;
        _isEof = false;

        int bits = (int)Math.Round(Math.Log2(avgSize));
        if (bits < 8) bits = 8;
        if (bits > 24) bits = 24;
        _mask = (1U << bits) - 1;
    }

    public async Task<byte[]?> NextChunkAsync(CancellationToken cancellationToken)
    {
        if (_bufferOffset == 0 && _isEof)
        {
            return null;
        }

        while (!_isEof && _bufferOffset < _maxSize)
        {
            int toRead = _maxSize - _bufferOffset;
            int read = await _stream.ReadAsync(_buffer.AsMemory(_bufferOffset, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _isEof = true;
                break;
            }
            _bufferOffset += read;
        }

        if (_bufferOffset == 0)
        {
            return null;
        }

        if (_isEof && _bufferOffset <= _minSize)
        {
            var finalChunk = new byte[_bufferOffset];
            Array.Copy(_buffer, 0, finalChunk, 0, _bufferOffset);
            _bufferOffset = 0;
            return finalChunk;
        }

        // Run Gear hash starting from _minSize
        int chunkLen = _minSize;
        uint hash = 0;
        for (; chunkLen < _bufferOffset; chunkLen++)
        {
            hash = (hash << 1) + GearTable[_buffer[chunkLen]];
            if ((hash & _mask) == 0)
            {
                chunkLen++;
                break;
            }
        }

        var chunk = new byte[chunkLen];
        Array.Copy(_buffer, 0, chunk, 0, chunkLen);

        int remaining = _bufferOffset - chunkLen;
        if (remaining > 0)
        {
            Array.Copy(_buffer, chunkLen, _buffer, 0, remaining);
        }
        _bufferOffset = remaining;

        return chunk;
    }
}
