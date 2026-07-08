namespace iBackup.Client.Core.Util;

/// <summary>
/// Read-only window over a section of an underlying stream. Used to stream one
/// chunk of a large encrypted file without loading it into memory.
/// Owns (and disposes) the underlying stream.
/// </summary>
public sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;

    public SubStream(Stream inner, long start, long length)
    {
        if (!inner.CanRead || !inner.CanSeek)
        {
            throw new ArgumentException("Underlying stream must be readable and seekable.", nameof(inner));
        }
        _inner = inner;
        _start = start;
        _length = Math.Min(length, Math.Max(0, inner.Length - start));
        _inner.Position = _start;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _inner.Position - _start;
        set => _inner.Position = _start + Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - Position;
        if (remaining <= 0)
        {
            return 0;
        }
        return _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = _length - Position;
        if (remaining <= 0)
        {
            return 0;
        }
        var slice = buffer.Length > remaining ? buffer[..(int)remaining] : buffer;
        return await _inner.ReadAsync(slice, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = target;
        return Position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
