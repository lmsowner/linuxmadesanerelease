// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILinuxServiceModuleDataService
{
    Task<IReadOnlyList<LinuxServiceDefinition>> ListServicesAsync(CancellationToken cancellationToken = default);

    Task<LinuxServiceDefinition?> GetServiceAsync(Guid serviceId, CancellationToken cancellationToken = default);

    Task SaveServiceAsync(LinuxServiceDefinition service, CancellationToken cancellationToken = default);

    Task DeleteServiceAsync(Guid serviceId, CancellationToken cancellationToken = default);

    Task<ServiceControlResult> ControlServiceAsync(
        Guid serviceId,
        ServiceControlAction action,
        CancellationToken cancellationToken = default);

    Task<ServiceInspectionResult> InspectServiceAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<ServiceUpdateIssueReport> GetUpdateIssueReportAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<ServiceUpdatePlan> GetUpdatePlanAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<ServiceRepairPlan> GetRepairPlanAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceDeploymentPattern>> ListDeploymentPatternsAsync(
        CancellationToken cancellationToken = default);
}
