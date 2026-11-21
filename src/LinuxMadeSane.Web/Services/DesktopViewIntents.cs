// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Collections.Concurrent;
using System.Security.Cryptography;
namespace LinuxMadeSane.Web.Services;

public sealed class DesktopViewIntents(TimeProvider clock)
{
    private sealed record Intent(Guid Owner, Guid Host, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Intent> pending = new(StringComparer.Ordinal);
    public string Issue(Guid owner, Guid host)
    {
        foreach (var entry in pending.Where(entry => entry.Value.Expires <= clock.GetUtcNow())) pending.TryRemove(entry);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        pending[key] = new(owner, host, clock.GetUtcNow().AddMinutes(2)); return key;
    }
    public bool Consume(string? key, Guid owner, Guid host) => !string.IsNullOrEmpty(key) && pending.TryGetValue(key, out var intent) &&
        intent.Owner == owner && intent.Host == host && intent.Expires > clock.GetUtcNow() && pending.TryRemove(new KeyValuePair<string, Intent>(key, intent));
}
