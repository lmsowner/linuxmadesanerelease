// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolApprovalMetadata(
    AiActionRiskLevel RiskLevel,
    AiApprovalRequirement MinimumRequirement,
    bool RequiresCommandPreview,
    bool SupportsApproveOnce,
    bool SupportsRememberDecision);
