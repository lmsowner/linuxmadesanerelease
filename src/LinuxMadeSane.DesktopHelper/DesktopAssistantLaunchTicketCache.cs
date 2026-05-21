// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

public sealed class DesktopAssistantLaunchTicketCache
{
    private readonly object syncRoot = new();
    private DesktopAssistantLaunchTicket? current;

    public void Update(DesktopAssistantLaunchTicket ticket)
    {
        lock (syncRoot)
        {
            current = ticket;
        }
    }

    public string? TryTakeToken()
    {
        lock (syncRoot)
        {
            if (current is null || current.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                current = null;
                return null;
            }

            var token = current.Token;
            current = null;
            return token;
        }
    }
}
