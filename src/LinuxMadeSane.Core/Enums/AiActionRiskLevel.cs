// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
