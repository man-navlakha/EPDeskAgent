namespace EPDeskAgent.Services;

internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public LimitedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _remaining = length;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var bytesRead = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= bytesRead;
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var bytesRead = await _inner.ReadAsync(
            buffer[..(int)Math.Min(buffer.Length, _remaining)],
            cancellationToken
        );

        _remaining -= bytesRead;
        return bytesRead;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return ReadLegacyAsync(buffer, offset, count, cancellationToken);
    }

    private async Task<int> ReadLegacyAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var bytesRead = await _inner.ReadAsync(
            buffer.AsMemory(offset, (int)Math.Min(count, _remaining)),
            cancellationToken
        );

        _remaining -= bytesRead;
        return bytesRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
