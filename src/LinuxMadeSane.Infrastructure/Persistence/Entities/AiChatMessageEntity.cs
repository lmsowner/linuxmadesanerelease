// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiChatMessageEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public int SequenceNumber { get; set; }
    public int Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public AiChatThreadEntity? Thread { get; set; }
}
