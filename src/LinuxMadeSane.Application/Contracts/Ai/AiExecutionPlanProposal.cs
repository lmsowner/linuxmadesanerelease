// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiExecutionPlanProposal
{
    public Guid? MessageId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<AiProposedActionProposal> Actions { get; set; } = [];
}
