// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LinuxMadeSane.Web.Services;

public sealed class ConnectedUserDnsResolver
{
    private readonly ConcurrentDictionary<string, CachedLookup> lookups = new(StringComparer.Ordinal);

    public Task<string?> ResolveAsync(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || !IsLocalAddress(ip)) return Task.FromResult<string?>(null);
        var now = DateTimeOffset.UtcNow;
        var lookup = lookups.AddOrUpdate(address,
            _ => new CachedLookup(now.AddMinutes(5), LookupAsync(ip)),
            (_, previous) => previous.ExpiresAt > now ? previous : new CachedLookup(now.AddMinutes(5), LookupAsync(ip)));
        foreach (var entry in lookups)
            if (entry.Value.ExpiresAt <= now) lookups.TryRemove(entry);
        return lookup.Result;
    }

    public static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    private static async Task<string?> LookupAsync(IPAddress address)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token);
            return IPAddress.TryParse(entry.HostName, out _) ? null : entry.HostName;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record CachedLookup(DateTimeOffset ExpiresAt, Task<string?> Result);
}
