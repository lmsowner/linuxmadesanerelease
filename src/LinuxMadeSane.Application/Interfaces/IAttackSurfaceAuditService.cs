// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAttackSurfaceAuditService
{
    Task<AttackSurfaceAiAvailability> GetAiAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<AttackSurfaceAuditResult> RunAsync(
        bool includeAiAnalysis,
        CancellationToken cancellationToken = default);
}
