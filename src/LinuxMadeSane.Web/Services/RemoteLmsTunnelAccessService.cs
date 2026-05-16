// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LinuxMadeSane.Web.Services;

public sealed class RemoteLmsTunnelAccessService
{
    public const string CookieName = "lms.remote-tunnel";
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<string, RemoteLmsTunnelGrant> grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteLmsTunnelSession> sessions = new(StringComparer.Ordinal);

    public RemoteLmsTunnelGrantIssueResult IssueGrant(string? requestedPath)
    {
        CleanupExpired(DateTimeOffset.UtcNow);

        var token = CreateToken();
        var tokenHash = HashToken(token);
        var now = DateTimeOffset.UtcNow;
        var expiresAtUtc = now.Add(GrantLifetime);
        grants[tokenHash] = new RemoteLmsTunnelGrant(
            NormalizeReturnUrl(requestedPath),
            expiresAtUtc);

        return new RemoteLmsTunnelGrantIssueResult(token, expiresAtUtc);
    }

    public RemoteLmsTunnelSessionIssueResult? ConsumeGrant(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        CleanupExpired(DateTimeOffset.UtcNow);

        var tokenHash = HashToken(token.Trim());
        if (!grants.TryRemove(tokenHash, out var grant) ||
            grant.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var sessionToken = CreateToken();
        var sessionTokenHash = HashToken(sessionToken);
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(SessionLifetime);
        sessions[sessionTokenHash] = new RemoteLmsTunnelSession(expiresAtUtc);

        return new RemoteLmsTunnelSessionIssueResult(
            sessionToken,
            grant.ReturnUrl,
            expiresAtUtc);
    }

    public bool IsAuthorized(IPAddress? remoteIpAddress, string? sessionToken)
    {
        if (remoteIpAddress is null ||
            !IPAddress.IsLoopback(remoteIpAddress) ||
            string.IsNullOrWhiteSpace(sessionToken))
        {
            return false;
        }

        CleanupExpired(DateTimeOffset.UtcNow);

        var sessionTokenHash = HashToken(sessionToken.Trim());
        return sessions.TryGetValue(sessionTokenHash, out var session) &&
               session.ExpiresAtUtc > DateTimeOffset.UtcNow;
    }

    public static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var trimmed = returnUrl.Trim();
        return trimmed.StartsWith("/", StringComparison.Ordinal) &&
               !trimmed.StartsWith("//", StringComparison.Ordinal) &&
               !trimmed.StartsWith("/\\", StringComparison.Ordinal)
            ? trimmed
            : "/";
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var (key, value) in grants)
        {
            if (value.ExpiresAtUtc <= now)
            {
                grants.TryRemove(key, out _);
            }
        }

        foreach (var (key, value) in sessions)
        {
            if (value.ExpiresAtUtc <= now)
            {
                sessions.TryRemove(key, out _);
            }
        }
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private sealed record RemoteLmsTunnelGrant(
        string ReturnUrl,
        DateTimeOffset ExpiresAtUtc);

    private sealed record RemoteLmsTunnelSession(DateTimeOffset ExpiresAtUtc);
}

public sealed record RemoteLmsTunnelGrantIssueResult(
    string Token,
    DateTimeOffset ExpiresAtUtc);

public sealed record RemoteLmsTunnelSessionIssueResult(
    string SessionToken,
    string ReturnUrl,
    DateTimeOffset ExpiresAtUtc);
