// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.Web.Services;

public sealed class DesktopAssistantLaunchTicketStore : IDesktopAssistantLaunchTicketStore
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DesktopAssistantLaunchTicket> tickets = new(StringComparer.Ordinal);

    public DesktopAssistantLaunchTicket Issue(string returnUrl)
    {
        PruneExpired();

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var ticket = new DesktopAssistantLaunchTicket(
            token,
            NormalizeReturnUrl(returnUrl),
            DateTimeOffset.UtcNow.Add(TicketLifetime));
        tickets[token] = ticket;
        return ticket;
    }

    public bool TryConsume(string? token, out string returnUrl)
    {
        returnUrl = "/";
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (!tickets.TryRemove(token.Trim(), out var ticket) ||
            ticket.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        returnUrl = ticket.ReturnUrl;
        return true;
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var ticket in tickets.Values.Where(ticket => ticket.ExpiresAtUtc <= now).ToArray())
        {
            tickets.TryRemove(ticket.Token, out _);
        }
    }

    private static string NormalizeReturnUrl(string? returnUrl)
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
}
