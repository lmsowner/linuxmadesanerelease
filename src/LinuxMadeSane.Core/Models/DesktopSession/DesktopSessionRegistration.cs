// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.DesktopSession;

public sealed record DesktopSessionRegistration(
    string ConnectionId,
    DesktopSessionCapabilityReport CapabilityReport,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    bool IsConnected);

public sealed record DesktopSessionBrokerSnapshot(
    IReadOnlyList<DesktopSessionRegistration> Sessions,
    DateTimeOffset GeneratedAtUtc)
{
    public DesktopSessionRegistration? BestAvailableSession =>
        Sessions
            .Where(session => session.IsConnected && session.CapabilityReport.IsGuiSessionAvailable)
            .OrderByDescending(session => session.LastSeenAtUtc)
            .FirstOrDefault();
}
