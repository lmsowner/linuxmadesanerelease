// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web.Services;

// Independent HTTP range cursors share one remote handle. No document-sized buffer.
internal sealed class PreviewReadSession(Func<CancellationToken, Task<Stream>> open) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Stream? source;
    private long length;
    private int revoked;

    public async Task<Stream> OpenAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (source is null)
            {
                var opened = await open(linked.Token);
                try
                {
                    if (!opened.CanSeek) throw new NotSupportedException("Document preview requires a seekable source for byte-range requests.");
                    length = opened.Length;
                    source = opened;
                }
                catch { await opened.DisposeAsync(); throw; }
            }
            linked.Token.ThrowIfCancellationRequested();
            return new RangeCursor(this, length);
        }
        finally { gate.Release(); }
    }

    private async ValueTask<int> ReadAsync(long position, Memory<byte> buffer, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (source is null) throw new IOException("The remote document connection closed. Retry the preview.");
            if (source.Position != position) source.Seek(position, SeekOrigin.Begin);
            return await source.ReadAsync(buffer, linked.Token);
        }
        catch (Exception) when (!linked.IsCancellationRequested)
        {
            var failed = source; source = null;
            if (failed is not null) try { await failed.DisposeAsync(); } catch { /* Preserve the read failure. */ }
            throw;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref revoked, 1) != 0) return;
        await lifetime.CancelAsync();
        await gate.WaitAsync();
        try { if (source is not null) await source.DisposeAsync(); }
        catch (IOException) { /* A disconnected remote stream may already be closed. */ }
        finally { gate.Release(); }
        // Keep the cancelled token available to outstanding HTTP cursors.
    }

    private sealed class RangeCursor(PreviewReadSession session, long length) : Stream
    {
        private long position;
        private bool disposed;
        public override bool CanRead => !disposed;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
            if (next < 0) throw new IOException("Cannot seek before the start of the document.");
            return position = next;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var count = (int)Math.Min(buffer.Length, Math.Max(0, length - position));
            if (count == 0) return 0;
            var read = await session.ReadAsync(position, buffer[..count], cancellationToken);
            position += read;
            return read;
        }
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
