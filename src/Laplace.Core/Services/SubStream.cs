using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Laplace.Core.Services;

public sealed class SubStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _startOffset;
    private readonly long _length;
    private long _position;

    public SubStream(Stream baseStream, long offset, long length)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
        
        _startOffset = offset;
        _length = length;
        _position = 0;
    }

    public override bool CanRead => _baseStream.CanRead;
    public override bool CanSeek => _baseStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value), "Position must be within SubStream bounds.");
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
            return 0;

        long remaining = _length - _position;
        int toRead = (int)Math.Min(count, remaining);

        _baseStream.Position = _startOffset + _position;
        int bytesRead = _baseStream.Read(buffer, offset, toRead);
        _position += bytesRead;
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_position >= _length)
            return 0;

        long remaining = _length - _position;
        int toRead = (int)Math.Min(count, remaining);

        _baseStream.Position = _startOffset + _position;
        int bytesRead = await _baseStream.ReadAsync(buffer, offset, toRead, cancellationToken).ConfigureAwait(false);
        _position += bytesRead;
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _length)
            return 0;

        long remaining = _length - _position;
        int toRead = (int)Math.Min(buffer.Length, remaining);

        _baseStream.Position = _startOffset + _position;
        int bytesRead = await _baseStream.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        _position += bytesRead;
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentException("Invalid seek origin.", nameof(origin))
        };

        if (newPosition < 0 || newPosition > _length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Seek destination is out of SubStream bounds.");

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException("SubStream does not support SetLength.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("SubStream does not support Write.");
    public override void Flush() => _baseStream.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _baseStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
