// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiExecutionPlanExecutionService
{
    Task ExecuteApprovedPlanAsync(Guid executionPlanId, CancellationToken cancellationToken = default);
}
