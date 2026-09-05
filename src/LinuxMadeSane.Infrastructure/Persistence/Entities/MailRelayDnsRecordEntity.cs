// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class MailRelayDnsRecordEntity
{
    public Guid Id { get; set; }
    public Guid MailRelayDomainId { get; set; }
    public string CloudflareRecordId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public bool CreatedByLms { get; set; }
    public bool ModifiedByLms { get; set; }
    public string? OriginalValue { get; set; }
    public string CurrentValue { get; set; } = string.Empty;
    public int ChangeType { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}
