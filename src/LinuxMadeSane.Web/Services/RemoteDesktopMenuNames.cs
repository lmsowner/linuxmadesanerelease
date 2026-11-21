// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RemoteDesktop;

namespace LinuxMadeSane.Web.Services;

public sealed class RemoteDesktopMenuNames(PageStatusCache cache, IServiceScopeFactory scopes)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> labels = new();
    public string CachedOrPending(RemoteDesktopConnection connection) => PreferredName(connection)
        ?? labels.GetValueOrDefault((connection.Name, connection.Hostname), PendingName(connection));

    public static string? PreferredName(RemoteDesktopConnection connection)
    {
        var friendly = connection.Name.Trim();
        if (friendly.Length > 0 && !IPAddress.TryParse(friendly, out _)) return friendly;
        var endpoint = connection.Hostname.Trim().TrimEnd('.');
        return Uri.CheckHostName(endpoint) == UriHostNameType.Dns ? endpoint : null;
    }

    public static string PendingName(RemoteDesktopConnection connection) =>
        PreferredName(connection) ?? $"Resolving {connection.Protocol} name ({connection.Hostname})…";

    public async Task<string> ResolveAsync(RemoteDesktopConnection connection, CancellationToken token)
    {
        if (PreferredName(connection) is { } friendly) return friendly;
        // Share the Remote Desktop table's user-partitioned DNS cache. No credential
        // labels, discovery scan, port probe or connection is involved in naming.
        var dns = await cache.GetAsync("remote-desktop", ("dns", connection.Hostname), async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISshHostDiscoveryService>()
                .ResolveRemoteDesktopDnsNameAsync(connection.Hostname, token);
        });
        var label = string.IsNullOrWhiteSpace(dns) || Uri.CheckHostName(dns) != UriHostNameType.Dns
            ? $"Unresolved {connection.Protocol} name ({connection.Hostname})" : dns;
        labels[(connection.Name, connection.Hostname)] = label;
        return label;
    }
}
