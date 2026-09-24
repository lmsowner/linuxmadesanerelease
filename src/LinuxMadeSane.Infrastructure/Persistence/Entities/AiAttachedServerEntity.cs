// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiAttachedServerEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid ManagedHostId { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public DateTimeOffset AttachedAtUtc { get; set; }

    public AiChatThreadEntity? Thread { get; set; }
    public ManagedHostEntity? ManagedHost { get; set; }
}
