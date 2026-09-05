// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class MailRelayConfigurationEntity
{
    public Guid Id { get; set; }
    public bool Enabled { get; set; }
    public string RelayHostname { get; set; } = string.Empty;
    public string PublicIpAddress { get; set; } = string.Empty;
    public int SubmissionPort { get; set; }
    public bool AllowTailscale { get; set; }
    public bool AllowTrustedLan { get; set; }
    public bool AllowPublicSubmission { get; set; }
    public int DeliveryMode { get; set; }
    public bool AllowLegacyPort25 { get; set; }
    public string LegacyListenAddressesJson { get; set; } = "[]";
    public string LegacyAllowedNetworksJson { get; set; } = "[]";
    public bool MonitorPublicIpChanges { get; set; }
    public int PublicIpCheckIntervalMinutes { get; set; } = 60;
    public DateTimeOffset? LastPublicIpCheckUtc { get; set; }
    public DateTimeOffset? LastPublicIpChangeUtc { get; set; }
    public int PublicIpMonitorStatus { get; set; }
    public string PublicIpMonitorDetail { get; set; } = string.Empty;
    public int DefaultMessagesPerMinute { get; set; }
    public int DefaultMessagesPerDay { get; set; }
    public int QueueLimit { get; set; }
    public int LogRetentionDays { get; set; }
    public string? TlsCertificateSecretReference { get; set; }
    public string? TlsPrivateKeySecretReference { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}
