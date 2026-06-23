using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Laplace.Core.Services;

public sealed class HttpRangeStream : Stream
{
    private static readonly HttpClient _httpClient = new();
    private readonly string _url;
    private long _position;
    private long? _length;

    // Cache parameters for read-ahead
    private readonly byte[] _cache = new byte[64 * 1024]; // 64KB cache
    private long _cacheOffset = -1;
    private int _cacheLength = 0;

    public HttpRangeStream(string url)
    {
        _url = url;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            if (!_length.HasValue)
            {
                _length = GetLengthFromServer();
            }
            return _length.Value;
        }
    }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Position must be non-negative");
            _position = value;
        }
    }

    private long GetLengthFromServer()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, _url);
            using var response = _httpClient.Send(request);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength.HasValue)
            {
                return response.Content.Headers.ContentLength.Value;
            }
        }
        catch
        {
            // Fallback to GET range 0-0
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = _httpClient.Send(request);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentRange?.Length.HasValue == true)
            {
                return response.Content.Headers.ContentRange.Length.Value;
            }
            if (response.Content.Headers.ContentLength.HasValue)
            {
                return response.Content.Headers.ContentLength.Value;
            }
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to retrieve content length from remote URL: {_url}", ex);
        }

        throw new IOException($"Could not determine content length for: {_url}");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentException("Invalid SeekOrigin", nameof(origin))
        };

        if (newPos < 0) throw new IOException("An attempt was made to move the file pointer before the beginning of the file.");
        _position = newPos;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;
        if (_position >= Length) return 0;

        if (count <= _cache.Length)
        {
            if (_cacheOffset != -1 && _position >= _cacheOffset && _position + count <= _cacheOffset + _cacheLength)
            {
                int cacheReadOffset = (int)(_position - _cacheOffset);
                Array.Copy(_cache, cacheReadOffset, buffer, offset, count);
                _position += count;
                return count;
            }

            long fetchOffset = _position;
            int fetchLength = (int)Math.Min(_cache.Length, Length - fetchOffset);

            int bytesFetched = FetchRange(fetchOffset, fetchLength, _cache, 0);
            if (bytesFetched <= 0) return 0;

            _cacheOffset = fetchOffset;
            _cacheLength = bytesFetched;

            int actualRead = Math.Min(count, bytesFetched);
            Array.Copy(_cache, 0, buffer, offset, actualRead);
            _position += actualRead;
            return actualRead;
        }
        else
        {
            int bytesRead = FetchRange(_position, count, buffer, offset);
            _position += bytesRead;
            return bytesRead;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count <= 0) return 0;
        if (_position >= Length) return 0;

        if (count <= _cache.Length)
        {
            if (_cacheOffset != -1 && _position >= _cacheOffset && _position + count <= _cacheOffset + _cacheLength)
            {
                int cacheReadOffset = (int)(_position - _cacheOffset);
                Array.Copy(_cache, cacheReadOffset, buffer, offset, count);
                _position += count;
                return count;
            }

            long fetchOffset = _position;
            int fetchLength = (int)Math.Min(_cache.Length, Length - fetchOffset);

            int bytesFetched = await FetchRangeAsync(fetchOffset, fetchLength, _cache, 0, cancellationToken).ConfigureAwait(false);
            if (bytesFetched <= 0) return 0;

            _cacheOffset = fetchOffset;
            _cacheLength = bytesFetched;

            int actualRead = Math.Min(count, bytesFetched);
            Array.Copy(_cache, 0, buffer, offset, actualRead);
            _position += actualRead;
            return actualRead;
        }
        else
        {
            int bytesRead = await FetchRangeAsync(_position, count, buffer, offset, cancellationToken).ConfigureAwait(false);
            _position += bytesRead;
            return bytesRead;
        }
    }

    private int FetchRange(long offset, int length, byte[] destBuffer, int destOffset)
    {
        if (length <= 0) return 0;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
            using var response = _httpClient.Send(request);
            response.EnsureSuccessStatusCode();

            using var contentStream = response.Content.ReadAsStream();
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = contentStream.Read(destBuffer, destOffset + totalRead, length - totalRead);
                if (read <= 0) break;
                totalRead += read;
            }
            return totalRead;
        }
        catch (Exception ex)
        {
            throw new IOException($"HTTP Range request failed for range {offset}-{offset + length - 1}", ex);
        }
    }

    private async Task<int> FetchRangeAsync(long offset, int length, byte[] destBuffer, int destOffset, CancellationToken cancellationToken)
    {
        if (length <= 0) return 0;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = await contentStream.ReadAsync(destBuffer.AsMemory(destOffset + totalRead, length - totalRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                totalRead += read;
            }
            return totalRead;
        }
        catch (Exception ex)
        {
            throw new IOException($"HTTP Range request failed for range {offset}-{offset + length - 1}", ex);
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
