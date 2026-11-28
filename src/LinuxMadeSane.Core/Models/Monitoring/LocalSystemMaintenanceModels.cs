// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Monitoring;

public sealed record LocalDiskCleanupResult(
    long? ReclaimedBytes,
    IReadOnlyList<LocalDiskCleanupStepResult> Steps)
{
    public bool HasFailures => Steps.Any(static step => !step.Succeeded);
}

public sealed record LocalDiskCleanupStepResult(
    string Name,
    bool Succeeded,
    string Detail);
