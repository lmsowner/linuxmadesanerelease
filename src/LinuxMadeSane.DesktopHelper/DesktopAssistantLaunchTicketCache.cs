// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

public sealed class DesktopAssistantLaunchTicketCache
{
    private readonly object syncRoot = new();
    private DesktopAssistantLaunchTicket? current;
    private TaskCompletionSource<string>? ticketAvailable;
    private DateTimeOffset? lastUpdatedAtUtc;
    private DateTimeOffset? lastInvalidatedAtUtc;

    public void Update(DesktopAssistantLaunchTicket ticket)
    {
        TaskCompletionSource<string>? waiter;
        lock (syncRoot)
        {
            current = ticket;
            lastUpdatedAtUtc = DateTimeOffset.UtcNow;
            waiter = ticketAvailable;
            ticketAvailable = null;
        }

        waiter?.TrySetResult(ticket.Token);
    }

    public async Task<string?> WaitForTokenAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Task<string> waitTask;
        lock (syncRoot)
        {
            var token = TryPeekTokenLocked();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }

            ticketAvailable ??= CreateWaiter();
            waitTask = ticketAvailable.Task;
        }

        try
        {
            return await waitTask.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public string? TryPeekToken()
    {
        lock (syncRoot)
        {
            return TryPeekTokenLocked();
        }
    }

    public void Invalidate(string token)
    {
        lock (syncRoot)
        {
            if (current is not null &&
                string.Equals(current.Token, token, StringComparison.Ordinal))
            {
                current = null;
                lastInvalidatedAtUtc = DateTimeOffset.UtcNow;
                ticketAvailable ??= CreateWaiter();
            }
        }
    }

    public DesktopAssistantLaunchTicketDebugSnapshot GetDebugSnapshot()
    {
        lock (syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            var hasTicket = current is not null;
            var hasValidTicket = current is not null && current.ExpiresAtUtc > now;
            return new DesktopAssistantLaunchTicketDebugSnapshot(
                hasTicket,
                hasValidTicket,
                current?.ExpiresAtUtc,
                PreviewToken(current?.Token),
                lastUpdatedAtUtc,
                lastInvalidatedAtUtc,
                ticketAvailable is not null && !ticketAvailable.Task.IsCompleted);
        }
    }

    public static string PreviewToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "none";
        }

        return token.Length <= 8 ? token : $"{token[..8]}...";
    }

    private string? TryPeekTokenLocked()
    {
        if (current is null || current.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            current = null;
            return null;
        }

        return current.Token;
    }

    private static TaskCompletionSource<string> CreateWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record DesktopAssistantLaunchTicketDebugSnapshot(
    bool HasTicket,
    bool HasValidTicket,
    DateTimeOffset? ExpiresAtUtc,
    string TokenPreview,
    DateTimeOffset? LastUpdatedAtUtc,
    DateTimeOffset? LastInvalidatedAtUtc,
    bool WaiterActive);
