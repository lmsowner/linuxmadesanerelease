// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiExecutionPlanProposal
{
    public Guid? MessageId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<AiProposedActionProposal> Actions { get; set; } = [];
}
