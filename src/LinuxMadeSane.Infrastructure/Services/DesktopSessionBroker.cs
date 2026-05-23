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
    private readonly ConcurrentDictionary<string, Func<DesktopSessionBrokerMessage, CancellationToken, Task>> notificationSenders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, PendingDesktopSessionAction> pendingActions = [];
    private readonly ConcurrentDictionary<Guid, PendingDesktopEvidenceRefresh> pendingEvidenceRefreshes = [];
    private readonly TimeSpan staleAfter = TimeSpan.FromSeconds(Math.Max(15, options.Value.StaleAfterSeconds));

    public Task RegisterOrRefreshAsync(
        string connectionId,
        DesktopSessionCapabilityReport capabilityReport,
        CancellationToken cancellationToken = default,
        bool preserveReadOnlyDiagnosticsWhenEmpty = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        sessions.AddOrUpdate(
            connectionId,
            _ => new DesktopSessionRegistration(connectionId, capabilityReport, now, now, true),
            (_, current) => current with
            {
                CapabilityReport = preserveReadOnlyDiagnosticsWhenEmpty && capabilityReport.ReadOnlyDiagnostics.Count == 0
                    ? capabilityReport with { ReadOnlyDiagnostics = current.CapabilityReport.ReadOnlyDiagnostics }
                    : capabilityReport,
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
        UnregisterNotificationSender(connectionId);
        foreach (var pending in pendingEvidenceRefreshes.Values.Where(pending => pending.ConnectionId == connectionId).ToArray())
        {
            if (pendingEvidenceRefreshes.TryRemove(pending.RequestId, out var removed))
            {
                removed.Completion.TrySetException(new InvalidOperationException("Desktop helper disconnected before the evidence refresh completed."));
            }
        }
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

    public void RegisterNotificationSender(
        string connectionId,
        Func<DesktopSessionBrokerMessage, CancellationToken, Task> sendAsync) =>
        notificationSenders[connectionId] = sendAsync;

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

    public void UnregisterNotificationSender(string connectionId) =>
        notificationSenders.TryRemove(connectionId, out _);

    public async Task PublishThemeChangedAsync(
        DesktopAssistantNativeTheme theme,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var message = new DesktopSessionBrokerMessage(
            DesktopSessionBrokerMessageTypes.ThemeChanged,
            null,
            DateTimeOffset.UtcNow)
        {
            Theme = theme
        };

        foreach (var sender in notificationSenders.ToArray())
        {
            try
            {
                await sender.Value(message, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                UnregisterNotificationSender(sender.Key);
            }
        }
    }

    public async Task<DesktopSessionBrokerSnapshot> RefreshEvidenceAsync(
        string connectionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!notificationSenders.TryGetValue(connectionId, out var sendAsync))
        {
            throw new InvalidOperationException("Desktop helper is not connected for evidence refresh.");
        }

        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<DesktopSessionCapabilityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingDesktopEvidenceRefresh(connectionId, requestId, completion);
        if (!pendingEvidenceRefreshes.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException("Desktop evidence refresh request id is already pending.");
        }

        try
        {
            var message = new DesktopSessionBrokerMessage(
                DesktopSessionBrokerMessageTypes.RefreshEvidence,
                null,
                DateTimeOffset.UtcNow)
            {
                EvidenceRequestId = requestId
            };
            await sendAsync(message, cancellationToken);
            await completion.Task.WaitAsync(timeout, cancellationToken);
            return GetSnapshot();
        }
        finally
        {
            pendingEvidenceRefreshes.TryRemove(requestId, out _);
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

    public Task CompleteEvidenceRefreshAsync(
        string connectionId,
        Guid requestId,
        DesktopSessionCapabilityReport capabilityReport,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pendingEvidenceRefreshes.TryGetValue(requestId, out var pending) &&
            string.Equals(pending.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            pending.Completion.TrySetResult(capabilityReport);
        }

        return Task.CompletedTask;
    }

    private sealed record PendingDesktopSessionAction(
        string ConnectionId,
        Guid RequestId,
        TaskCompletionSource<DesktopSessionActionResult> Completion);

    private sealed record PendingDesktopEvidenceRefresh(
        string ConnectionId,
        Guid RequestId,
        TaskCompletionSource<DesktopSessionCapabilityReport> Completion);
}
