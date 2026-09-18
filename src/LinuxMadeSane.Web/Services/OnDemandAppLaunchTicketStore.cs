// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace LinuxMadeSane.Web.Services;

public sealed class OnDemandAppLaunchTicketStore(TimeProvider timeProvider)
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, LaunchTicket> tickets = new(StringComparer.Ordinal);

    public string Issue(
        ClaimsPrincipal principal,
        string expectedHost,
        DateTimeOffset? sessionIssuedUtc,
        DateTimeOffset? sessionExpiresUtc)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("An authenticated LMS account is required.");
        }

        var normalizedHost = NormalizeHost(expectedHost);
        RemoveExpired();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var claims = principal.Claims.Select(claim => new Claim(
            claim.Type,
            claim.Value,
            claim.ValueType,
            claim.Issuer,
            claim.OriginalIssuer)).ToArray();
        tickets[HashToken(token)] = new LaunchTicket(
            claims,
            normalizedHost,
            timeProvider.GetUtcNow().Add(TicketLifetime),
            sessionIssuedUtc,
            sessionExpiresUtc);
        return token;
    }

    public ConsumedOnDemandAppTicket? Consume(string token, string requestHost)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var key = HashToken(token.Trim());
        if (!tickets.TryGetValue(key, out var ticket) ||
            ticket.ExpiresUtc <= timeProvider.GetUtcNow() ||
            !ticket.ExpectedHost.Equals(NormalizeHost(requestHost), StringComparison.OrdinalIgnoreCase) ||
            !tickets.TryRemove(new KeyValuePair<string, LaunchTicket>(key, ticket)))
        {
            return null;
        }

        var identity = new ClaimsIdentity(ticket.Claims, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        return new ConsumedOnDemandAppTicket(
            new ClaimsPrincipal(identity),
            ticket.ExpectedHost,
            ticket.SessionIssuedUtc,
            ticket.SessionExpiresUtc);
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in tickets.Where(item => item.Value.ExpiresUtc <= now))
        {
            tickets.TryRemove(item.Key, out _);
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NormalizeHost(string host) =>
        string.IsNullOrWhiteSpace(host)
            ? throw new ArgumentException("A host is required.", nameof(host))
            : host.Trim().TrimEnd('.').ToLowerInvariant();

    private sealed record LaunchTicket(
        IReadOnlyList<Claim> Claims,
        string ExpectedHost,
        DateTimeOffset ExpiresUtc,
        DateTimeOffset? SessionIssuedUtc,
        DateTimeOffset? SessionExpiresUtc);
}

public sealed record ConsumedOnDemandAppTicket(
    ClaimsPrincipal Principal,
    string ExpectedHost,
    DateTimeOffset? SessionIssuedUtc,
    DateTimeOffset? SessionExpiresUtc);
