namespace DumpTriage.Common.Upload;

/// <summary>
/// Read-only pass-through over the dump file that records when the HTTP
/// stack last pulled bytes from it, and whether it has read to the end. The
/// upload's watchdog uses that instead of a whole-request timeout (website#1488):
/// a slow line that keeps moving is fine, a connection that stopped is not.
/// </summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private long _lastActivityTicks = Environment.TickCount64;
    private volatile bool _completed;

    public ProgressStream(Stream inner) => _inner = inner;

    /// <summary>Milliseconds since bytes were last read (or since the stream was made).</summary>
    public long MillisecondsSinceActivity => Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks);

    /// <summary>The whole body has been read: from here on the server is working, not the line.</summary>
    public bool Completed => _completed;

    private int Track(int read)
    {
        Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);
        if (read == 0) _completed = true;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => Track(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Track(_inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Track(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Track(await _inner.ReadAsync(buffer, cancellationToken));

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
