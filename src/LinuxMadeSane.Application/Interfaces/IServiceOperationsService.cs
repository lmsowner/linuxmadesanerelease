using LinuxMadeSane.Application.Contracts.Services;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Interfaces;

public interface IServiceOperationsService
{
    Task<ServiceDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<ServiceEditor> GetEditorAsync(Guid? serviceId, CancellationToken cancellationToken = default);

    Task<Guid> SaveServiceAsync(ServiceEditor editor, CancellationToken cancellationToken = default);

    Task DeleteServiceAsync(Guid serviceId, CancellationToken cancellationToken = default);

    Task<ServiceControlResult> ControlServiceAsync(
        Guid serviceId,
        ServiceControlAction action,
        CancellationToken cancellationToken = default);

    Task<ServiceDetailsViewModel?> GetDetailsAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<ServiceInspectionResult?> GetInspectionAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<ServiceHelperViewModel?> GetHelperAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<SaneUpdaterViewModel?> GetUpdaterAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<ServiceRepairToolViewModel?> GetRepairToolAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<ServiceIssueFixerViewModel?> GetIssueFixerAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);

    Task<ServiceDeploymentPatternsViewModel> GetDeploymentPatternsAsync(
        CancellationToken cancellationToken = default);
}
