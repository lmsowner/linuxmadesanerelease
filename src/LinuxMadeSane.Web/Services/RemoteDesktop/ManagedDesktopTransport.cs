// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
namespace LinuxMadeSane.Web.Services.RemoteDesktop;

// A session-owned relay bound only to the managed Docker bridge. Only guacd may connect.
// Remote traffic stays inside the existing authenticated SSH connection; no LAN RDP port is opened.
public sealed class ManagedDesktopTransport : IDisposable, IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly IPAddress peer;
    private readonly int destination;
    private readonly SshClient? ssh;
    private readonly ForwardedPortLocal? forward;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly Task accepting;
    private int disposed;
    private readonly Func<Task>? release;
    private Task cleanup = Task.CompletedTask;
    internal DesktopShareLease? ShareLease { get; }
    internal bool IsActive => Volatile.Read(ref disposed) == 0;
    public string Hostname { get; }
    public int Port { get; }

    public ManagedDesktopTransport(IPAddress gateway, IPAddress peer, int destination, SshClient? ssh = null, ForwardedPortLocal? forward = null, DesktopShareLease? share = null, Func<Task>? release = null, Func<Task<bool>>? renew = null)
    {
        this.peer = peer; this.destination = destination; this.ssh = ssh; this.forward = forward;
        ShareLease = share; this.release = release;
        listener = new(gateway, 0); listener.Start();
        Hostname = gateway.ToString(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
        if (renew is not null) _ = RenewAsync(renew);
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || !endpoint.Address.Equals(peer)) { client.Dispose(); continue; }
                clients.TryAdd(client, 0);
                _ = RelayAsync(client);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        using (var target = new TcpClient())
        {
            try
            {
                await target.ConnectAsync(IPAddress.Loopback, destination, lifetime.Token);
                using var incoming = client.GetStream(); using var outgoing = target.GetStream();
                var upload = incoming.CopyToAsync(outgoing, lifetime.Token); var download = outgoing.CopyToAsync(incoming, lifetime.Token);
                await Task.WhenAny(upload, download);
                client.Dispose(); target.Dispose();
                try { await Task.WhenAll(upload, download); } catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { clients.TryRemove(client, out _); }
        }
    }
    private async Task RenewAsync(Func<Task<bool>> renew)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) if (!await renew()) { Dispose(); return; } }
        catch (OperationCanceledException) { }
        catch { Dispose(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); listener.Stop();
        foreach (var client in clients.Keys) client.Dispose();
        forward?.Dispose(); ssh?.Dispose();
        if (release is not null) cleanup = Task.Run(release);
    }
    public async ValueTask DisposeAsync() { Dispose(); await cleanup; }
}
