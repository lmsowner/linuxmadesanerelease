// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LinuxMadeSane.Web.Services;

// Carry an authenticated inventory click to the details page without making a
// visit to an ordinary host URL authorize installation or a reload repeat it.
public sealed class LmsHostSetupLaunches(TimeProvider clock)
{
    private sealed record Launch(Guid Owner, Guid Host, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Launch> pending = new(StringComparer.Ordinal);

    public string Issue(Guid owner, Guid host)
    {
        foreach (var entry in pending.Where(entry => entry.Value.Expires <= clock.GetUtcNow())) pending.TryRemove(entry);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        pending[key] = new(owner, host, clock.GetUtcNow().AddMinutes(2));
        return key;
    }

    public bool Consume(string? key, Guid owner, Guid host) => !string.IsNullOrEmpty(key)
        && pending.TryGetValue(key, out var launch) && launch.Owner == owner && launch.Host == host
        && launch.Expires > clock.GetUtcNow() && pending.TryRemove(new KeyValuePair<string, Launch>(key, launch));
}
