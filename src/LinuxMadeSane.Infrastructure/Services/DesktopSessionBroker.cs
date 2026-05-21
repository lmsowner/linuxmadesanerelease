// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.DesktopSession;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DesktopSessionBroker(IOptions<DesktopSessionBrokerOptions> options) : IDesktopSessionBroker
{
    private readonly ConcurrentDictionary<string, DesktopSessionRegistration> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<DesktopSessionActionRequest, CancellationToken, Task>> actionSenders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, PendingDesktopSessionAction> pendingActions = [];
    private readonly TimeSpan staleAfter = TimeSpan.FromSeconds(Math.Max(15, options.Value.StaleAfterSeconds));

    public Task RegisterOrRefreshAsync(
        string connectionId,
        DesktopSessionCapabilityReport capabilityReport,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        sessions.AddOrUpdate(
            connectionId,
            _ => new DesktopSessionRegistration(connectionId, capabilityReport, now, now, true),
            (_, current) => current with
            {
                CapabilityReport = capabilityReport,
                LastSeenAtUtc = now,
                IsConnected = true
            });

        return Task.CompletedTask;
    }

    public void MarkDisconnected(string connectionId)
    {
        if (sessions.TryGetValue(connectionId, out var current))
        {
            sessions[connectionId] = current with
            {
                IsConnected = false,
                LastSeenAtUtc = DateTimeOffset.UtcNow
            };
        }

        UnregisterActionSender(connectionId);
    }

    public DesktopSessionBrokerSnapshot GetSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var activeCutoff = now.Subtract(staleAfter);
        var snapshot = sessions.Values
            .Select(session => session.LastSeenAtUtc >= activeCutoff
                ? session
                : session with { IsConnected = false })
            .OrderByDescending(session => session.IsConnected)
            .ThenByDescending(session => session.LastSeenAtUtc)
            .ToArray();

        return new DesktopSessionBrokerSnapshot(snapshot, now);
    }

    public void RegisterActionSender(
        string connectionId,
        Func<DesktopSessionActionRequest, CancellationToken, Task> sendAsync) =>
        actionSenders[connectionId] = sendAsync;

    public void UnregisterActionSender(string connectionId)
    {
        actionSenders.TryRemove(connectionId, out _);
        foreach (var pending in pendingActions.Values.Where(pending => pending.ConnectionId == connectionId).ToArray())
        {
            if (pendingActions.TryRemove(pending.RequestId, out var removed))
            {
                removed.Completion.TrySetException(new InvalidOperationException("Desktop helper disconnected before the action completed."));
            }
        }
    }

    public async Task<DesktopSessionActionResult> ExecuteActionAsync(
        string connectionId,
        DesktopSessionActionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!actionSenders.TryGetValue(connectionId, out var sendAsync))
        {
            throw new InvalidOperationException("Desktop helper is not connected for action requests.");
        }

        var completion = new TaskCompletionSource<DesktopSessionActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingDesktopSessionAction(connectionId, request.RequestId, completion);
        if (!pendingActions.TryAdd(request.RequestId, pending))
        {
            throw new InvalidOperationException("Desktop action request id is already pending.");
        }

        try
        {
            await sendAsync(request, cancellationToken);
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            pendingActions.TryRemove(request.RequestId, out _);
        }
    }

    public Task CompleteActionAsync(
        string connectionId,
        DesktopSessionActionResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pendingActions.TryGetValue(result.RequestId, out var pending) &&
            string.Equals(pending.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            pending.Completion.TrySetResult(result);
        }

        return Task.CompletedTask;
    }

    private sealed record PendingDesktopSessionAction(
        string ConnectionId,
        Guid RequestId,
        TaskCompletionSource<DesktopSessionActionResult> Completion);
}
