// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Net.WebSockets;

namespace LinuxMadeSane.Connect.Protocol.Transport;

/// <summary>A bounded-buffer duplex stream over a binary WebSocket. No document buffering.</summary>
public sealed class PortalWebSocketStream(WebSocket socket, long maximumBytes = 512L * 1024 * 1024) : Stream
{
    private readonly SemaphoreSlim writer = new(1);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long transferred;
    private int disposed;
    public Task Closed => closed.Task;
    public override bool CanRead => disposed == 0;
    public override bool CanWrite => disposed == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private void Count(int bytes)
    {
        if (Interlocked.Add(ref transferred, bytes) > maximumBytes)
            throw new IOException("The Portal session transfer limit was reached. Use a direct connection for bulk transfers.");
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) { closed.TrySetResult(); return 0; }
            if (result.MessageType != WebSocketMessageType.Binary) throw new IOException("Invalid Portal tunnel frame.");
            if (result.Count == 0) continue;
            Count(result.Count); return result.Count;
        }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        await writer.WaitAsync(token);
        try
        {
            Count(buffer.Length);
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, 16384);
                await socket.SendAsync(buffer[..count], WebSocketMessageType.Binary, true, token);
                buffer = buffer[count..];
            }
        }
        finally { writer.Release(); }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) { socket.Abort(); socket.Dispose(); closed.TrySetResult(); }
        base.Dispose(disposing);
    }
}
