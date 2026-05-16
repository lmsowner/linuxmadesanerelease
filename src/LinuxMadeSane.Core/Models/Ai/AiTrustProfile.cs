// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiTrustProfile(
    AiTrustLevel TrustLevel,
    bool AllowReadOnlyTools,
    bool AllowMutatingTools,
    bool RequireApprovalForMediumRisk,
    bool RequireApprovalForHighRisk)
{
    public static AiTrustProfile CreatePreset(AiTrustLevel trustLevel) => trustLevel switch
    {
        AiTrustLevel.Observe => new AiTrustProfile(trustLevel, false, false, true, true),
        AiTrustLevel.Guided => new AiTrustProfile(trustLevel, true, false, true, true),
        AiTrustLevel.OperatorApproved => new AiTrustProfile(trustLevel, true, true, true, true),
        AiTrustLevel.Elevated => new AiTrustProfile(trustLevel, true, true, false, true),
        _ => new AiTrustProfile(AiTrustLevel.Guided, true, false, true, true)
    };
}
