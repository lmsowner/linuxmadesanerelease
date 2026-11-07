// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Models.RemoteDesktop;
namespace LinuxMadeSane.Web.Services.RemoteDesktop;

public sealed class RemoteDesktopSessionRegistry(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Grant> grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, RemoteDesktopSession> sessions = new();
    public ConcurrentQueue<RemoteDesktopSession> PendingCleanup { get; } = new();
    public IReadOnlyList<RemoteDesktopSession> Active => sessions.Values.ToArray();
    public RemoteDesktopSession Create(RemoteDesktopConnection connection, string cookieBinding, DateTimeOffset expires, DateTimeOffset? userUpdatedAtUtc = null, string? trustedRemoteAddress = null, string? trustedRequestHost = null)
    {
        if (connection.OwnerUserId == Guid.Empty && (trustedRemoteAddress is null || trustedRequestHost is null) || expires <= clock.GetUtcNow()) throw new UnauthorizedAccessException();
        var session = new RemoteDesktopSession(Guid.NewGuid(), connection, cookieBinding, expires) { UserUpdatedAtUtc = userUpdatedAtUtc ?? default, TrustedRemoteAddress = trustedRemoteAddress, TrustedRequestHost = trustedRequestHost };
        sessions[session.Id] = session; return session;
    }
    public string Issue(RemoteDesktopSession session)
    {
        foreach (var expired in grants.Where(g => g.Value.Expires <= clock.GetUtcNow())) grants.TryRemove(expired.Key, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        grants[Hash(token)] = new(session.Id, session.Connection.OwnerUserId, session.Connection.Id, clock.GetUtcNow().AddSeconds(30));
        return token;
    }
    public RemoteDesktopSession? Consume(string token, Guid user, Guid connection, Guid session)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        if (!grants.TryGetValue(hash, out var grant) || grant.Expires <= clock.GetUtcNow() || grant.User != user ||
            grant.Connection != connection || grant.Session != session || !grants.TryRemove(new KeyValuePair<string, Grant>(hash, grant))) return null;
        return Get(session);
    }
    public RemoteDesktopSession? Get(Guid id) => sessions.TryGetValue(id, out var session) &&
        session.Expires > clock.GetUtcNow() && !session.Revoked.IsCancellationRequested ? session : null;
    public RemoteDesktopSession? GetOwned(Guid id, Guid user, string binding) => Get(id) is { } session &&
        session.Connection.OwnerUserId == user && session.CookieBinding == binding ? session : null;
    public void Revoke(RemoteDesktopSession session) { if (sessions.TryRemove(session.Id, out _)) { session.Revoked.Cancel(); PendingCleanup.Enqueue(session); } }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Grant(Guid Session, Guid User, Guid Connection, DateTimeOffset Expires);
}
public sealed class RemoteDesktopSession(Guid id, RemoteDesktopConnection connection, string binding, DateTimeOffset expires)
{
    internal SemaphoreSlim AuthenticationGate { get; } = new(1, 1);
    private long lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    internal DateTimeOffset LastActivity => new(Interlocked.Read(ref lastActivityTicks),TimeSpan.Zero);
    internal void Touch() => Interlocked.Exchange(ref lastActivityTicks,DateTimeOffset.UtcNow.UtcTicks);
    public Guid Id { get; } = id;
    public RemoteDesktopConnection Connection { get; } = connection;
    public string CookieBinding { get; } = binding;
    public DateTimeOffset Expires { get; } = expires;
    internal string? TrustedRemoteAddress { get; init; }
    internal string? TrustedRequestHost { get; init; }
    internal DateTimeOffset UserUpdatedAtUtc { get; set; }
    public CancellationTokenSource Revoked { get; } = new();
    // Actual Guacamole tokens never leave the LMS process or enter browser storage/URLs.
    internal string GuacamoleToken { get; set; } = "";
    internal string HttpTunnelToken { get; set; } = "";
    internal string BrowserAuthJson { get; set; } = "";
    public override string ToString() => $"Remote desktop session {Id} (tokens redacted)";
}
