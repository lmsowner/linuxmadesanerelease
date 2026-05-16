// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum AiActionRiskLevel
{
    ReadOnly = 0,
    LowRiskMutation = 1,
    MediumRiskMutation = 2,
    HighRiskMutation = 3,
    Destructive = 4,
    Privileged = 5,
    NetworkOrSecuritySensitive = 6
}
