// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAttackSurfaceAuditService
{
    Task<AttackSurfaceAiAvailability> GetAiAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<AttackSurfaceAuditResult> RunAsync(
        bool includeAiAnalysis,
        CancellationToken cancellationToken = default);
}
