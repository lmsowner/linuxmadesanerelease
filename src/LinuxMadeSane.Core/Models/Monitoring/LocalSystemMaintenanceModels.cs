// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
