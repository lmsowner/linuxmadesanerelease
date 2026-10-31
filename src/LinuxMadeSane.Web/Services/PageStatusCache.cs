// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web.Services;

/// <summary>User-partitioned display caching that survives page/circuit navigation.</summary>
public sealed class PageStatusCache
{
    private readonly PageStatusCacheStore store;
    private readonly Func<Task<Guid?>> user;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public PageStatusCache(TimeProvider clock) : this(new PageStatusCacheStore(clock), () => Task.FromResult<Guid?>(Guid.Empty)) { }
    public PageStatusCache(PageStatusCacheStore store, Func<Task<Guid?>> user)
    {
        this.store = store;
        this.user = user;
    }

    public async Task<T> GetAsync<T>(string area, object key, Func<Task<T>> load)
    {
        var id = await user();
        return id is null ? await load() : await store.GetAsync(id.Value + ":" + area, key, load);
    }

    public async Task InvalidateAsync(string area)
    {
        if (await user() is { } id) store.Invalidate(id + ":" + area);
    }
}

/// <summary>Only non-secret observations; access is checked by the scoped facade.</summary>
public sealed class PageStatusCacheStore(TimeProvider clock)
{
    private readonly object sync = new();
    private readonly Dictionary<(string Area, object Key), Entry> entries = [];

    public async Task<T> GetAsync<T>(string area, object key, Func<Task<T>> load)
    {
        Entry entry;
        lock (sync)
        {
            // Discard expired display data, including entries for removed hosts.
            foreach (var old in entries.Where(x => x.Value.Users == 0 && x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
                entries.Remove(old);
            if (!entries.TryGetValue((area, key), out entry!)) entries[(area, key)] = entry = new();
            entry.Users++;
        }
        await entry.Gate.WaitAsync();
        try
        {
            if (entry.HasValue && entry.Expires > clock.GetUtcNow()) return (T)entry.Value!;
            var value = await load(); // Failed/cancelled loads are never cached.
            entry.Value = value;
            entry.Expires = clock.GetUtcNow() + PageStatusCache.Lifetime;
            entry.HasValue = true;
            return value;
        }
        finally { entry.Gate.Release(); lock (sync) entry.Users--; }
    }

    public void Invalidate(string area)
    {
        lock (sync)
            foreach (var key in entries.Keys.Where(x => x.Area == area).ToArray()) entries.Remove(key);
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object? Value;
        public DateTimeOffset Expires;
        public bool HasValue;
        public int Users;
    }
}
