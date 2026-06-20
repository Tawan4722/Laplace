using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Laplace.Core.Services;

public sealed class MultiVolumeStream : Stream
{
    private readonly string _basePath;
    private readonly bool _isWrite;
    private readonly long? _volumeLimit;

    private readonly List<string> _volumePaths = new();
    private readonly List<Stream> _openStreams = new();
    private readonly List<long> _volumeLengths = new();

    private long _position;
    private long _length;

    // Read mode constructor
    public MultiVolumeStream(string firstVolumePath)
    {
        _basePath = firstVolumePath;
        _isWrite = false;
        _volumeLimit = null;

        int idx = 1;
        while (true)
        {
            string path = GetVolumePath(_basePath, idx);
            if (File.Exists(path))
            {
                _volumePaths.Add(path);
                long len = new FileInfo(path).Length;
                _volumeLengths.Add(len);
                _length += len;
                _openStreams.Add(null!);
                idx++;
            }
            else
            {
                break;
            }
        }

        if (_volumePaths.Count == 0)
        {
            throw new FileNotFoundException($"Could not find the first volume: {firstVolumePath}");
        }
    }

    // Write mode constructor (new creation)
    public MultiVolumeStream(string basePath, long volumeLimit)
    {
        if (volumeLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(volumeLimit), "Volume limit must be greater than zero.");

        _basePath = basePath;
        _isWrite = true;
        _volumeLimit = volumeLimit;
        _position = 0;
        _length = 0;
    }

    // In-place ReadWrite mode constructor (used for repair of existing volumes)
    public MultiVolumeStream(List<string> volumePaths, bool isWrite)
    {
        if (volumePaths == null || volumePaths.Count == 0)
            throw new ArgumentException("Volume paths cannot be empty.", nameof(volumePaths));

        _basePath = volumePaths[0];
        _isWrite = isWrite;
        _volumeLimit = null;

        _length = 0;
        foreach (var path in volumePaths)
        {
            _volumePaths.Add(path);
            long len = File.Exists(path) ? new FileInfo(path).Length : 0;
            _volumeLengths.Add(len);
            _length += len;
            _openStreams.Add(null!);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _isWrite;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public static string GetVolumePath(string basePath, int index)
    {
        if (string.IsNullOrEmpty(basePath))
            return basePath;

        if (basePath.EndsWith(".001", StringComparison.OrdinalIgnoreCase))
        {
            return basePath[..^3] + index.ToString("D3");
        }

        var extension = Path.GetExtension(basePath);
        if (extension.Length == 4 && char.IsDigit(extension[1]) && char.IsDigit(extension[2]) && char.IsDigit(extension[3]))
        {
            return basePath[..^3] + index.ToString("D3");
        }

        return basePath + "." + index.ToString("D3");
    }

    public static bool IsMultiVolumeFirstFile(string path, out string firstVolumePath)
    {
        firstVolumePath = path;
        if (string.IsNullOrEmpty(path))
            return false;

        if (path.EndsWith(".001", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            return true;
        }

        if (path.EndsWith(".lpc", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = path + ".001";
            if (File.Exists(candidate))
            {
                firstVolumePath = candidate;
                return true;
            }
        }

        var extension = Path.GetExtension(path);
        if (extension.Length == 4 && char.IsDigit(extension[1]) && char.IsDigit(extension[2]) && char.IsDigit(extension[3]))
        {
            if (File.Exists(path))
            {
                return true;
            }
        }

        return false;
    }

    public static List<string> GetVolumePaths(string path)
    {
        var paths = new List<string>();
        if (IsMultiVolumeFirstFile(path, out string firstVolPath))
        {
            int idx = 1;
            while (true)
            {
                string p = GetVolumePath(firstVolPath, idx);
                if (File.Exists(p))
                {
                    paths.Add(p);
                    idx++;
                }
                else
                {
                    break;
                }
            }
        }

        if (paths.Count == 0 && File.Exists(path))
        {
            paths.Add(path);
        }
        return paths;
    }

    private void GetVolumeIndexAndOffset(long position, out int volumeIndex, out long offset)
    {
        if (!_isWrite || _volumeLimit == null)
        {
            if (position < 0)
                throw new ArgumentOutOfRangeException(nameof(position));

            long accum = 0;
            for (int i = 0; i < _volumeLengths.Count; i++)
            {
                long nextAccum = accum + _volumeLengths[i];
                if (position < nextAccum)
                {
                    volumeIndex = i;
                    offset = position - accum;
                    return;
                }
                accum = nextAccum;
            }
            volumeIndex = Math.Max(0, _volumeLengths.Count - 1);
            offset = _volumeLengths.Count > 0 ? _volumeLengths[^1] : 0;
        }
        else
        {
            long limit = _volumeLimit.Value;
            volumeIndex = (int)(position / limit);
            offset = position % limit;
        }
    }

    private Stream GetStream(int volumeIndex)
    {
        while (_openStreams.Count <= volumeIndex)
        {
            _openStreams.Add(null!);
        }

        if (_openStreams[volumeIndex] == null)
        {
            string path;
            if (!_isWrite)
            {
                path = _volumePaths[volumeIndex];
                _openStreams[volumeIndex] = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else
            {
                if (_volumeLimit != null)
                {
                    path = GetVolumePath(_basePath, volumeIndex + 1);
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    _openStreams[volumeIndex] = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                else
                {
                    path = _volumePaths[volumeIndex];
                    _openStreams[volumeIndex] = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            }
        }
        return _openStreams[volumeIndex];
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
            return 0;

        int totalBytesRead = 0;
        while (totalBytesRead < count && _position < _length)
        {
            GetVolumeIndexAndOffset(_position, out int volumeIndex, out long volumeOffset);
            long volLength = _volumeLengths[volumeIndex];
            long remainingInVolume = volLength - volumeOffset;

            if (remainingInVolume <= 0)
            {
                if (volumeIndex + 1 >= _volumePaths.Count)
                {
                    break;
                }
                volumeIndex++;
                volumeOffset = 0;
                remainingInVolume = _volumeLengths[volumeIndex];
            }

            int toRead = (int)Math.Min(count - totalBytesRead, remainingInVolume);
            if (toRead <= 0)
                break;

            var stream = GetStream(volumeIndex);
            stream.Position = volumeOffset;
            int read = stream.Read(buffer, offset + totalBytesRead, toRead);
            if (read <= 0)
                break;

            _position += read;
            totalBytesRead += read;
        }
        return totalBytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_position >= _length)
            return 0;

        int totalBytesRead = 0;
        while (totalBytesRead < count && _position < _length)
        {
            GetVolumeIndexAndOffset(_position, out int volumeIndex, out long volumeOffset);
            
            long remainingInVolume;
            if (_volumeLimit != null)
            {
                remainingInVolume = _volumeLimit.Value - volumeOffset;
            }
            else
            {
                remainingInVolume = _volumeLengths[volumeIndex] - volumeOffset;
            }

            if (remainingInVolume <= 0)
            {
                if (_volumeLimit != null)
                {
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = _volumeLimit.Value;
                }
                else
                {
                    if (volumeIndex + 1 >= _volumePaths.Count)
                    {
                        break;
                    }
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = _volumeLengths[volumeIndex];
                }
            }

            int toRead = (int)Math.Min(count - totalBytesRead, remainingInVolume);
            if (toRead <= 0)
                break;

            var stream = GetStream(volumeIndex);
            stream.Position = volumeOffset;
            int read = await stream.ReadAsync(buffer, offset + totalBytesRead, toRead, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            _position += read;
            totalBytesRead += read;
        }
        return totalBytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _length)
            return 0;

        int totalBytesRead = 0;
        int count = buffer.Length;
        while (totalBytesRead < count && _position < _length)
        {
            GetVolumeIndexAndOffset(_position, out int volumeIndex, out long volumeOffset);
            
            long remainingInVolume;
            if (_volumeLimit != null)
            {
                remainingInVolume = _volumeLimit.Value - volumeOffset;
            }
            else
            {
                remainingInVolume = _volumeLengths[volumeIndex] - volumeOffset;
            }

            if (remainingInVolume <= 0)
            {
                if (_volumeLimit != null)
                {
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = _volumeLimit.Value;
                }
                else
                {
                    if (volumeIndex + 1 >= _volumePaths.Count)
                    {
                        break;
                    }
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = _volumeLengths[volumeIndex];
                }
            }

            int toRead = (int)Math.Min(count - totalBytesRead, remainingInVolume);
            if (toRead <= 0)
                break;

            var stream = GetStream(volumeIndex);
            stream.Position = volumeOffset;
            int read = await stream.ReadAsync(buffer.Slice(totalBytesRead, toRead), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            _position += read;
            totalBytesRead += read;
        }
        return totalBytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!_isWrite)
            throw new NotSupportedException("Stream is not writable.");

        int bytesWritten = 0;
        while (bytesWritten < count)
        {
            GetVolumeIndexAndOffset(_position, out int volumeIndex, out long volumeOffset);

            long remainingInVolume;
            if (_volumeLimit != null)
            {
                long limit = _volumeLimit.Value;
                remainingInVolume = limit - volumeOffset;
                if (remainingInVolume <= 0)
                {
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = limit;
                }
            }
            else
            {
                long volLength = _volumeLengths[volumeIndex];
                remainingInVolume = volLength - volumeOffset;
                if (remainingInVolume <= 0 && volumeIndex + 1 < _volumePaths.Count)
                {
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = _volumeLengths[volumeIndex];
                }
            }

            int toWrite = (int)Math.Min(count - bytesWritten, remainingInVolume);
            if (toWrite <= 0)
            {
                throw new IOException("Cannot write past the end of the multi-volume stream in in-place repair mode.");
            }

            var stream = GetStream(volumeIndex);
            stream.Position = volumeOffset;
            stream.Write(buffer, offset + bytesWritten, toWrite);

            _position += toWrite;
            bytesWritten += toWrite;
            if (_position > _length)
            {
                _length = _position;
            }
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (!_isWrite)
            throw new NotSupportedException("Stream is not writable.");

        int bytesWritten = 0;
        while (bytesWritten < count)
        {
            GetVolumeIndexAndOffset(_position, out int volumeIndex, out long volumeOffset);

            long remainingInVolume;
            if (_volumeLimit != null)
            {
                long limit = _volumeLimit.Value;
                remainingInVolume = limit - volumeOffset;
                if (remainingInVolume <= 0)
                {
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = limit;
                }
            }
            else
            {
                long volLength = _volumeLengths[volumeIndex];
                remainingInVolume = volLength - volumeOffset;
                if (remainingInVolume <= 0 && volumeIndex + 1 < _volumePaths.Count)
                {
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = _volumeLengths[volumeIndex];
                }
            }

            int toWrite = (int)Math.Min(count - bytesWritten, remainingInVolume);
            if (toWrite <= 0)
            {
                throw new IOException("Cannot write past the end of the multi-volume stream in in-place repair mode.");
            }

            var stream = GetStream(volumeIndex);
            stream.Position = volumeOffset;
            await stream.WriteAsync(buffer.AsMemory(offset + bytesWritten, toWrite), cancellationToken).ConfigureAwait(false);

            _position += toWrite;
            bytesWritten += toWrite;
            if (_position > _length)
            {
                _length = _position;
            }
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_isWrite)
            throw new NotSupportedException("Stream is not writable.");

        int bytesWritten = 0;
        int count = buffer.Length;
        while (bytesWritten < count)
        {
            GetVolumeIndexAndOffset(_position, out int volumeIndex, out long volumeOffset);

            long remainingInVolume;
            if (_volumeLimit != null)
            {
                long limit = _volumeLimit.Value;
                remainingInVolume = limit - volumeOffset;
                if (remainingInVolume <= 0)
                {
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = limit;
                }
            }
            else
            {
                long volLength = _volumeLengths[volumeIndex];
                remainingInVolume = volLength - volumeOffset;
                if (remainingInVolume <= 0 && volumeIndex + 1 < _volumePaths.Count)
                {
                    volumeIndex++;
                    volumeOffset = 0;
                    remainingInVolume = _volumeLengths[volumeIndex];
                }
            }

            int toWrite = (int)Math.Min(count - bytesWritten, remainingInVolume);
            if (toWrite <= 0)
            {
                throw new IOException("Cannot write past the end of the multi-volume stream in in-place repair mode.");
            }

            var stream = GetStream(volumeIndex);
            stream.Position = volumeOffset;
            await stream.WriteAsync(buffer.Slice(bytesWritten, toWrite), cancellationToken).ConfigureAwait(false);

            _position += toWrite;
            bytesWritten += toWrite;
            if (_position > _length)
            {
                _length = _position;
            }
        }
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

        if (newPosition < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Seek destination cannot be negative.");

        if (!_isWrite && newPosition > _length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Seek destination is out of bounds.");

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("MultiVolumeStream does not support SetLength.");
    }

    public override void Flush()
    {
        foreach (var stream in _openStreams)
        {
            stream?.Flush();
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var stream in _openStreams)
        {
            if (stream != null)
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var stream in _openStreams)
            {
                stream?.Dispose();
            }
            _openStreams.Clear();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var stream in _openStreams)
        {
            if (stream != null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        _openStreams.Clear();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
