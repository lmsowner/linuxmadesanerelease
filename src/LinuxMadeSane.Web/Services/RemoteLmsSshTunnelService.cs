// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Sockets;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;

namespace LinuxMadeSane.Web.Services;

public sealed class RemoteLmsSshTunnelService(
    IServiceScopeFactory scopeFactory,
    ManagedHostSshConnectionFactory sshConnectionFactory,
    IHttpClientFactory httpClientFactory,
    RemoteLmsRelayCaddyService relayCaddyService,
    ILogger<RemoteLmsSshTunnelService> logger) : IDisposable
{
    private const int RemoteLmsPort = 5080;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TunnelReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TunnelReadyAttemptTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, RemoteLmsSshTunnel> tunnels = [];
    private bool disposed;

    public async Task<RemoteLmsSshTunnelConnection> ConnectAsync(
        Guid hostId,
        string path = "/",
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (tunnels.TryGetValue(hostId, out var existing))
            {
                if (existing.IsUsable)
                {
                    existing.Touch();
                    return await BuildRelayConnectionAsync(existing, path, reused: true, cancellationToken);
                }

                tunnels.Remove(hostId);
                existing.Dispose();
                await relayCaddyService.RemoveAsync(hostId, cancellationToken);
            }

            var host = await LoadHostAsync(hostId, cancellationToken)
                ?? throw new InvalidOperationException("The selected LMS host is no longer registered.");
            var capabilities = ManagedHostCapabilities.Describe(host);
            if (!capabilities.IsLmsHost || capabilities.IsLocalLmsHost)
            {
                throw new InvalidOperationException("SSH tunnelling is only used for registered remote LMS hosts.");
            }

            var credentials = await ResolveCredentialsAsync(host, cancellationToken);
            var client = sshConnectionFactory.CreateSshClient(host, credentials, ConnectTimeout, KeepAliveInterval);
            var tunnelCreated = false;

            try
            {
                await Task.Run(client.Connect, cancellationToken);
                if (!client.IsConnected)
                {
                    throw new InvalidOperationException("The SSH tunnel could not connect to the remote host.");
                }

                var forwardedPort = StartForwardedPort(host, client);
                var tunnel = new RemoteLmsSshTunnel(host, client, forwardedPort);
                tunnels[host.Id] = tunnel;
                tunnelCreated = true;

                try
                {
                    return await BuildRelayConnectionAsync(tunnel, path, reused: false, cancellationToken);
                }
                catch
                {
                    tunnels.Remove(host.Id);
                    tunnel.Dispose();
                    await relayCaddyService.RemoveAsync(host.Id, cancellationToken);
                    throw;
                }
            }
            catch
            {
                if (!tunnelCreated)
                {
                    client.Dispose();
                }

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RemoteLmsSshTunnelConnection> BuildRelayConnectionAsync(
        RemoteLmsSshTunnel tunnel,
        string path,
        bool reused,
        CancellationToken cancellationToken)
    {
        var connection = tunnel.ToConnection(path, reused);
        var client = httpClientFactory.CreateClient();
        await WaitForForwardedLmsReadyAsync(
            client,
            new Uri(new Uri(connection.BaseUrl), "/healthz"),
            cancellationToken);

        var relay = await relayCaddyService.PublishAsync(
            tunnel.Host,
            tunnel.LocalPort,
            path,
            cancellationToken);

        return connection with
        {
            TargetUrl = relay.Url,
            Url = relay.Url
        };
    }

    private static async Task WaitForForwardedLmsReadyAsync(
        HttpClient client,
        Uri healthUri,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        var stopAt = DateTimeOffset.UtcNow.Add(TunnelReadyTimeout);
        for (var attempt = 1; DateTimeOffset.UtcNow < stopAt; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            request.Headers.UserAgent.ParseAdd("LinuxMadeSane-LmsTunnel/1.0");

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TunnelReadyAttemptTimeout);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastException = new InvalidOperationException(
                    $"Remote LMS health returned {(int)response.StatusCode} {response.StatusCode}.");
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or OperationCanceledException) &&
                                       !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(300 + (attempt * 150), 900)), cancellationToken);
        }

        throw new InvalidOperationException(
            "The SSH tunnel opened, but the remote LMS web service did not become ready on port 5080.",
            lastException);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
        try
        {
            relayCaddyService.ClearAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear remote LMS Caddy relay routes during shutdown.");
        }

        foreach (var tunnel in tunnels.Values)
        {
            tunnel.Dispose();
        }

        tunnels.Clear();
    }

    private async Task<ManagedHost?> LoadHostAsync(Guid hostId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IManagedHostStore>();
        return await store.GetAsync(hostId, cancellationToken);
    }

    private async Task<ManagedHostSshCredentials> ResolveCredentialsAsync(
        ManagedHost host,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sshConnectionFactory.ResolveStoredCredentialsAsync(host, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "Save working SSH credentials for this LMS host before connecting through an SSH tunnel.",
                ex);
        }
    }

    private ForwardedPortLocal StartForwardedPort(ManagedHost host, SshClient client)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var localPort = AllocateLoopbackPort();
            var forwardedPort = new ForwardedPortLocal(
                IPAddress.Loopback.ToString(),
                (uint)localPort,
                IPAddress.Loopback.ToString(),
                RemoteLmsPort);

            forwardedPort.Exception += (_, args) =>
            {
                logger.LogWarning(
                    args.Exception,
                    "Remote LMS SSH tunnel for host {HostId} hit a forwarding error.",
                    host.Id);
            };

            try
            {
                client.AddForwardedPort(forwardedPort);
                forwardedPort.Start();
                logger.LogDebug(
                    "Remote LMS SSH tunnel for host {HostId} is listening on 127.0.0.1:{LocalPort}.",
                    host.Id,
                    localPort);
                return forwardedPort;
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                lastError = ex;
                try
                {
                    if (forwardedPort.IsStarted)
                    {
                        forwardedPort.Stop();
                    }

                    client.RemoveForwardedPort(forwardedPort);
                }
                catch
                {
                    // Best effort cleanup before trying another local port.
                }

                forwardedPort.Dispose();
            }
        }

        throw new InvalidOperationException(
            "Linux Made Sane could not reserve a local port for the SSH tunnel.",
            lastError);
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class RemoteLmsSshTunnel(
        ManagedHost host,
        SshClient client,
        ForwardedPortLocal forwardedPort) : IDisposable
    {
        public ManagedHost Host { get; } = host;

        public bool IsUsable => client.IsConnected && forwardedPort.IsStarted;

        public int LocalPort => checked((int)forwardedPort.BoundPort);

        private DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

        public void Touch() => LastUsedUtc = DateTimeOffset.UtcNow;

        public RemoteLmsSshTunnelConnection ToConnection(string path, bool reused)
        {
            var baseUri = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), LocalPort, "/").Uri;
            var targetUri = BuildTargetUri(baseUri, path);
            return new RemoteLmsSshTunnelConnection(
                Host.Id,
                Host.Name,
                baseUri.ToString(),
                targetUri.ToString(),
                targetUri.ToString(),
                LocalPort,
                reused,
                LastUsedUtc);
        }

        public void Dispose()
        {
            try
            {
                if (forwardedPort.IsStarted)
                {
                    forwardedPort.Stop();
                }
            }
            catch
            {
            }

            forwardedPort.Dispose();
            client.Dispose();
        }

        private static Uri BuildTargetUri(Uri baseUri, string? path)
        {
            var trimmedPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            if (!trimmedPath.StartsWith('/'))
            {
                trimmedPath = "/" + trimmedPath;
            }

            return new Uri(baseUri, trimmedPath);
        }
    }
}

public sealed record RemoteLmsTunnelGrantRequest(string? ReturnUrl);

public sealed record RemoteLmsTunnelGrantResponse(string Token, DateTimeOffset ExpiresAtUtc);

public sealed record RemoteLmsSshTunnelConnection(
    Guid HostId,
    string HostName,
    string BaseUrl,
    string TargetUrl,
    string Url,
    int LocalPort,
    bool Reused,
    DateTimeOffset LastUsedUtc);
