// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiExecutionPlanExecutionService
{
    Task ExecuteApprovedPlanAsync(Guid executionPlanId, CancellationToken cancellationToken = default);
}
