namespace EPDeskOldDataUploader.Services;

/// <summary>
/// Exposes exactly one multipart slice of a file. The inner stream is positioned
/// at the part offset by the caller; this caps how far a reader may run so the
/// same FileStream can serve one part without copying it into memory first.
/// </summary>
public sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;

    public LimitedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;

        if (remaining <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        _position += read;

        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var remaining = _length - _position;

        if (remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken);
        _position += read;

        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
