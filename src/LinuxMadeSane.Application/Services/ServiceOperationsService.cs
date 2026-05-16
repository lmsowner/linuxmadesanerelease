// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Services;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Services;

public sealed class ServiceOperationsService(ILinuxServiceModuleDataService serviceDataService) : IServiceOperationsService
{
    public async Task<ServiceDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var services = await serviceDataService.ListServicesAsync(cancellationToken);
        var highlightSource = services.FirstOrDefault();

        var highlightedIssues = highlightSource is null
            ? Array.Empty<ServiceRepairIssue>()
            : (await serviceDataService.GetRepairPlanAsync(highlightSource.Id, cancellationToken)).Issues.Take(3).ToArray();

        return new ServiceDashboardViewModel(services, highlightedIssues);
    }

    public async Task<ServiceEditor> GetEditorAsync(Guid? serviceId, CancellationToken cancellationToken = default)
    {
        if (!serviceId.HasValue)
        {
            return new ServiceEditor();
        }

        var service = await serviceDataService.GetServiceAsync(serviceId.Value, cancellationToken);
        return service is null ? new ServiceEditor { Id = serviceId } : MapEditor(service);
    }

    public async Task<Guid> SaveServiceAsync(ServiceEditor editor, CancellationToken cancellationToken = default)
    {
        var id = editor.Id ?? Guid.NewGuid();
        var definition = new LinuxServiceDefinition(
            id,
            editor.UnitName.Trim(),
            editor.DisplayName.Trim(),
            editor.HostName.Trim(),
            editor.Summary.Trim(),
            editor.RuntimeState,
            editor.HealthStatus,
            editor.EnabledAtBoot,
            editor.ActiveUnderSystemd,
            editor.RunningUser.Trim(),
            editor.RunningGroup.Trim(),
            editor.WorkingDirectory.Trim(),
            editor.ExecStart.Trim(),
            string.IsNullOrWhiteSpace(editor.EnvironmentFile) ? null : editor.EnvironmentFile.Trim(),
            editor.RestartCount,
            editor.LastStartTime,
            editor.ListeningPort,
            ParseCsv(editor.RecentErrorsCsv));

        await serviceDataService.SaveServiceAsync(definition, cancellationToken);
        return id;
    }

    public Task DeleteServiceAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
        serviceDataService.DeleteServiceAsync(serviceId, cancellationToken);

    public Task<ServiceControlResult> ControlServiceAsync(
        Guid serviceId,
        ServiceControlAction action,
        CancellationToken cancellationToken = default) =>
        serviceDataService.ControlServiceAsync(serviceId, action, cancellationToken);

    public async Task<ServiceDetailsViewModel?> GetDetailsAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var services = await serviceDataService.ListServicesAsync(cancellationToken);
        var selected = SelectService(services, serviceId);
        if (selected is null)
        {
            return null;
        }

        var inspection = await serviceDataService.InspectServiceAsync(selected.Id, cancellationToken);
        return new ServiceDetailsViewModel(services, inspection);
    }

    public async Task<ServiceInspectionResult?> GetInspectionAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var service = await serviceDataService.GetServiceAsync(serviceId, cancellationToken);
        if (service is null)
        {
            return null;
        }

        return await serviceDataService.InspectServiceAsync(serviceId, cancellationToken);
    }

    public async Task<ServiceHelperViewModel?> GetHelperAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var services = await serviceDataService.ListServicesAsync(cancellationToken);
        var selected = SelectService(services, serviceId);
        if (selected is null)
        {
            return null;
        }

        var inspection = await serviceDataService.InspectServiceAsync(selected.Id, cancellationToken);
        return new ServiceHelperViewModel(services, inspection);
    }

    public async Task<SaneUpdaterViewModel?> GetUpdaterAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var services = await serviceDataService.ListServicesAsync(cancellationToken);
        var selected = SelectService(services, serviceId);
        if (selected is null)
        {
            return null;
        }

        var updatePlan = await serviceDataService.GetUpdatePlanAsync(selected.Id, cancellationToken);
        return new SaneUpdaterViewModel(services, updatePlan);
    }

    public async Task<ServiceRepairToolViewModel?> GetRepairToolAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var services = await serviceDataService.ListServicesAsync(cancellationToken);
        var selected = SelectService(services, serviceId);
        if (selected is null)
        {
            return null;
        }

        var repairPlan = await serviceDataService.GetRepairPlanAsync(selected.Id, cancellationToken);
        return new ServiceRepairToolViewModel(services, repairPlan);
    }

    public async Task<ServiceIssueFixerViewModel?> GetIssueFixerAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var services = await serviceDataService.ListServicesAsync(cancellationToken);
        var selected = SelectService(services, serviceId);
        if (selected is null)
        {
            return null;
        }

        var issueReport = await serviceDataService.GetUpdateIssueReportAsync(selected.Id, cancellationToken);
        return new ServiceIssueFixerViewModel(services, issueReport);
    }

    public async Task<ServiceDeploymentPatternsViewModel> GetDeploymentPatternsAsync(CancellationToken cancellationToken = default)
    {
        var patterns = await serviceDataService.ListDeploymentPatternsAsync(cancellationToken);
        return new ServiceDeploymentPatternsViewModel(patterns);
    }

    private static LinuxServiceDefinition? SelectService(IReadOnlyList<LinuxServiceDefinition> services, Guid serviceId)
    {
        if (services.Count == 0)
        {
            return null;
        }

        return services.FirstOrDefault(item => item.Id == serviceId) ?? services[0];
    }

    private static ServiceEditor MapEditor(LinuxServiceDefinition service) =>
        new()
        {
            Id = service.Id,
            UnitName = service.UnitName,
            DisplayName = service.DisplayName,
            HostName = service.HostName,
            Summary = service.Summary,
            RuntimeState = service.RuntimeState,
            HealthStatus = service.HealthStatus,
            EnabledAtBoot = service.EnabledAtBoot,
            ActiveUnderSystemd = service.ActiveUnderSystemd,
            RunningUser = service.RunningUser,
            RunningGroup = service.RunningGroup,
            WorkingDirectory = service.WorkingDirectory,
            ExecStart = service.ExecStart,
            EnvironmentFile = service.EnvironmentFile ?? string.Empty,
            RestartCount = service.RestartCount,
            LastStartTime = service.LastStartTime,
            ListeningPort = service.ListeningPort,
            RecentErrorsCsv = string.Join(", ", service.RecentErrors)
        };

    private static IReadOnlyList<string> ParseCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
