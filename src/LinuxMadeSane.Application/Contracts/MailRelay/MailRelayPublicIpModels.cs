// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Application.Contracts.MailRelay;

public sealed record MailRelayPublicIpv4DetectionResult(
    bool Success,
    string Address,
    string Detail);

public sealed record MailRelayPublicIpMonitorSettingsRequest(
    bool Enabled,
    int CheckIntervalMinutes);

public sealed record MailRelayPublicIpDnsCheck(
    string Purpose,
    string RecordName,
    MailRelayDnsStatus Status,
    bool Changed,
    string Detail);

public sealed record MailRelayPublicIpSyncResult(
    bool Success,
    bool PublicIpChanged,
    string PreviousPublicIp,
    string CurrentPublicIp,
    MailRelayPublicIpMonitorStatus Status,
    IReadOnlyList<MailRelayPublicIpDnsCheck> DnsChecks,
    string Summary,
    DateTimeOffset CheckedAtUtc);
