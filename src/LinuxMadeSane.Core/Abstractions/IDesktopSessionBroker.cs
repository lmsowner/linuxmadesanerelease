// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.Core.Abstractions;

public interface IDesktopSessionBroker
{
    Task RegisterOrRefreshAsync(
        string connectionId,
        DesktopSessionCapabilityReport capabilityReport,
        CancellationToken cancellationToken = default);

    void MarkDisconnected(string connectionId);

    DesktopSessionBrokerSnapshot GetSnapshot();

    void RegisterActionSender(
        string connectionId,
        Func<DesktopSessionActionRequest, CancellationToken, Task> sendAsync);

    void UnregisterActionSender(string connectionId);

    Task<DesktopSessionActionResult> ExecuteActionAsync(
        string connectionId,
        DesktopSessionActionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task CompleteActionAsync(
        string connectionId,
        DesktopSessionActionResult result,
        CancellationToken cancellationToken = default);
}
