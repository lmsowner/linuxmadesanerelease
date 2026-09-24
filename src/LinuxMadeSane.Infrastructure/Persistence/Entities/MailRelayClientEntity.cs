// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class MailRelayClientEntity
{
    public Guid Id { get; set; }
    public Guid MailRelayConfigurationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string AllowedSenderDomainsJson { get; set; } = "[]";
    public string AllowedNetworksJson { get; set; } = "[]";
    public int MessagesPerMinute { get; set; }
    public int MessagesPerDay { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }
}
