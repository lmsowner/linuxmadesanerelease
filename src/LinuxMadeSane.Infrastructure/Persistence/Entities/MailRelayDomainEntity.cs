// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class MailRelayDomainEntity
{
    public Guid Id { get; set; }
    public Guid MailRelayConfigurationId { get; set; }
    public string CloudflareZoneId { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string CurrentDkimSelector { get; set; } = string.Empty;
    public string? CurrentDkimPrivateKeySecretReference { get; set; }
    public DateTimeOffset? CurrentDkimCreatedUtc { get; set; }
    public DateTimeOffset? CurrentDkimActivatedUtc { get; set; }
    public string? PreviousDkimSelector { get; set; }
    public string? PreviousDkimPrivateKeySecretReference { get; set; }
    public DateTimeOffset? PreviousDkimCreatedUtc { get; set; }
    public DateTimeOffset? PreviousDkimActivatedUtc { get; set; }
    public DateTimeOffset? PreviousDkimRetiredUtc { get; set; }
    public string? DkimCloudflareRecordId { get; set; }
    public string? SpfCloudflareRecordId { get; set; }
    public string? DmarcCloudflareRecordId { get; set; }
    public int SpfStatus { get; set; }
    public int DkimStatus { get; set; }
    public int DmarcStatus { get; set; }
    public int DmarcPolicy { get; set; }
    public string? DmarcReportingAddress { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}
