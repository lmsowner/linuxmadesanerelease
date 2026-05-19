// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiProposedActionProposal
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ProviderToolCallId { get; set; } = string.Empty;
    public string ToolArgumentsJson { get; set; } = string.Empty;
    public string CommandPreview { get; set; } = string.Empty;
    public AiActionRiskLevel RiskLevel { get; set; } = AiActionRiskLevel.ReadOnly;
    public AiSafeChangeState? SafeChange { get; set; }
}
