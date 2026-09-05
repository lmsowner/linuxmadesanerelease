// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.MailRelay;

public enum MailRelayOperationalStatus
{
    NotConfigured = 0,
    Healthy = 1,
    Warning = 2,
    Critical = 3
}

public enum MailRelayDnsStatus
{
    NotChecked = 0,
    Pending = 1,
    Pass = 2,
    Warning = 3,
    Failed = 4
}

public enum MailRelayDmarcPolicy
{
    Monitor = 0,
    Quarantine = 1,
    Reject = 2
}

public enum MailRelayDeliveryMode
{
    DirectInternet = 0,
    Microsoft365Relay = 1,
    UpstreamSmtp = 2
}

public enum MailRelayDnsChangeType
{
    ObservedExisting = 0,
    Created = 1,
    ModifiedShared = 2
}

public sealed record MailRelayConfiguration(
    Guid Id,
    bool Enabled,
    string RelayHostname,
    string PublicIpAddress,
    int SubmissionPort,
    bool AllowTailscale,
    bool AllowTrustedLan,
    bool AllowPublicSubmission,
    int DefaultMessagesPerMinute,
    int DefaultMessagesPerDay,
    int QueueLimit,
    int LogRetentionDays,
    string? TlsCertificateSecretReference,
    string? TlsPrivateKeySecretReference,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    MailRelayDeliveryMode DeliveryMode,
    bool AllowLegacyPort25,
    IReadOnlyList<string> LegacyListenAddresses,
    IReadOnlyList<string> LegacyAllowedNetworks)
{
    public IReadOnlyList<string> EffectiveLegacyListenAddresses => LegacyListenAddresses;
    public IReadOnlyList<string> EffectiveLegacyAllowedNetworks => LegacyAllowedNetworks;

    public static MailRelayConfiguration CreateDefault(DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            false,
            string.Empty,
            string.Empty,
            587,
            true,
            false,
            false,
            100,
            5_000,
            5_000,
            30,
            null,
            null,
            now,
            now,
            MailRelayDeliveryMode.DirectInternet,
            false,
            [],
            []);
}

public sealed record MailRelayDomain(
    Guid Id,
    Guid MailRelayConfigurationId,
    string CloudflareZoneId,
    string DomainName,
    bool Enabled,
    string CurrentDkimSelector,
    string? CurrentDkimPrivateKeySecretReference,
    DateTimeOffset? CurrentDkimCreatedUtc,
    DateTimeOffset? CurrentDkimActivatedUtc,
    string? PreviousDkimSelector,
    string? PreviousDkimPrivateKeySecretReference,
    DateTimeOffset? PreviousDkimCreatedUtc,
    DateTimeOffset? PreviousDkimActivatedUtc,
    DateTimeOffset? PreviousDkimRetiredUtc,
    string? DkimCloudflareRecordId,
    string? SpfCloudflareRecordId,
    string? DmarcCloudflareRecordId,
    MailRelayDnsStatus SpfStatus,
    MailRelayDnsStatus DkimStatus,
    MailRelayDnsStatus DmarcStatus,
    MailRelayDmarcPolicy DmarcPolicy,
    string? DmarcReportingAddress,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record MailRelayClient(
    Guid Id,
    Guid MailRelayConfigurationId,
    string Name,
    string Username,
    string PasswordHash,
    bool Enabled,
    IReadOnlyList<string> AllowedSenderDomains,
    IReadOnlyList<string> AllowedNetworks,
    int MessagesPerMinute,
    int MessagesPerDay,
    string Notes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastUsedUtc);

public sealed record MailRelayDnsRecord(
    Guid Id,
    Guid MailRelayDomainId,
    string CloudflareRecordId,
    string Type,
    string Name,
    string Purpose,
    bool CreatedByLms,
    bool ModifiedByLms,
    string? OriginalValue,
    string CurrentValue,
    MailRelayDnsChangeType ChangeType,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
