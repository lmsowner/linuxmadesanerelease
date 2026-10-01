// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;

namespace LinuxMadeSane.Web.Services;

public sealed record ConnectedUser(string? UserName, string SourceIpAddress, DateTimeOffset ConnectedAtUtc, int ConnectionCount);
public sealed record ConnectedUserSnapshot(int UserCount, int ConnectionCount, IReadOnlyList<ConnectedUser> Users);

public sealed class ConnectedUserRegistry(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Connection> connections = new(StringComparer.Ordinal);
    public event Action? Changed;

    public void Connect(string connectionId, ClaimsPrincipal user, IPAddress? sourceIpAddress)
    {
        var accountId = user.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        var userName = accountId is null ? null : user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.Email);
        var address = sourceIpAddress?.IsIPv4MappedToIPv6 == true ? sourceIpAddress.MapToIPv4() : sourceIpAddress;
        connections[connectionId] = new Connection(connectionId, accountId, userName, address?.ToString() ?? "Unknown", clock.GetUtcNow());
        Changed?.Invoke();
    }

    public void Disconnect(string connectionId)
    {
        if (connections.TryRemove(connectionId, out _)) Changed?.Invoke();
    }

    public ConnectedUserSnapshot GetSnapshot()
    {
        var active = connections.Values.ToArray();
        var users = active.GroupBy(item => (Identity: item.AccountId ?? (item.SourceIpAddress == "Unknown" ? item.ConnectionId : "guest:" + item.SourceIpAddress), item.SourceIpAddress))
            .Select(group => new ConnectedUser(group.First().UserName, group.Key.SourceIpAddress,
                group.Min(item => item.ConnectedAtUtc), group.Count()))
            .OrderBy(item => item.ConnectedAtUtc).ThenBy(item => item.SourceIpAddress, StringComparer.Ordinal).ToArray();
        return new ConnectedUserSnapshot(active.Select(item => item.AccountId ?? (item.SourceIpAddress == "Unknown" ? item.ConnectionId : "guest:" + item.SourceIpAddress)).Distinct(StringComparer.Ordinal).Count(), active.Length, users);
    }

    private sealed record Connection(string ConnectionId, string? AccountId, string? UserName, string SourceIpAddress, DateTimeOffset ConnectedAtUtc);
}
