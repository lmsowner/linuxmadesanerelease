// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiExecutionPlanEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? MessageId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int Outcome { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public AiChatThreadEntity? Thread { get; set; }
    public List<AiProposedActionEntity> ProposedActions { get; set; } = [];
}
