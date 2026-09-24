// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiToolInvocationEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? ExecutionPlanId { get; set; }
    public Guid? ProposedActionId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = string.Empty;
    public int Status { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public AiChatThreadEntity? Thread { get; set; }
    public AiToolResultEntity? Result { get; set; }
}
