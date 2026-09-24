// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiChatCheckpointEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? MessageId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string StateJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public AiChatThreadEntity? Thread { get; set; }
}
