// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.Core.Abstractions;

public interface IDesktopSessionBroker
{
    Task RegisterOrRefreshAsync(
        string connectionId,
        DesktopSessionCapabilityReport capabilityReport,
        CancellationToken cancellationToken = default,
        bool preserveReadOnlyDiagnosticsWhenEmpty = false);

    void MarkDisconnected(string connectionId);

    DesktopSessionBrokerSnapshot GetSnapshot();

    void RegisterActionSender(
        string connectionId,
        Func<DesktopSessionActionRequest, CancellationToken, Task> sendAsync);

    void RegisterNotificationSender(
        string connectionId,
        Func<DesktopSessionBrokerMessage, CancellationToken, Task> sendAsync);

    void UnregisterActionSender(string connectionId);

    void UnregisterNotificationSender(string connectionId);

    Task PublishThemeChangedAsync(
        DesktopAssistantNativeTheme theme,
        CancellationToken cancellationToken = default);

    Task<DesktopSessionBrokerSnapshot> RefreshEvidenceAsync(
        string connectionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<DesktopSessionActionResult> ExecuteActionAsync(
        string connectionId,
        DesktopSessionActionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task CompleteActionAsync(
        string connectionId,
        DesktopSessionActionResult result,
        CancellationToken cancellationToken = default);

    Task CompleteEvidenceRefreshAsync(
        string connectionId,
        Guid requestId,
        DesktopSessionCapabilityReport capabilityReport,
        CancellationToken cancellationToken = default);
}
