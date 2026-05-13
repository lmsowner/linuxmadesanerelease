using LinuxMadeSane.Application.Contracts.Cloudflare;
using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Interfaces;

public interface IExposedServiceManager
{
    Task<CloudflareExposeServiceWorkspaceViewModel> GetWorkspaceAsync(
        Guid hostId,
        CancellationToken cancellationToken = default);

    Task<CloudflareValidationResult> ValidateTokenAsync(
        Guid hostId,
        string? apiToken,
        CancellationToken cancellationToken = default);

    Task<ExposedServiceConnectorSetupResult> EnsureConnectorAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudflareDnsRecord>> ListZoneRecordsAsync(
        Guid hostId,
        string zoneId,
        string? apiToken = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(
        Guid hostId,
        string zoneId,
        string selectedAccountId,
        string? apiToken = null,
        CancellationToken cancellationToken = default);

    Task<ExposedServiceDryRunPlan> PreviewAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        string? currentUserEmail,
        CancellationToken cancellationToken = default);

    Task<ExposedServiceApplyResult> ApplyAsync(
        Guid hostId,
        CloudflareExposeServiceEditor editor,
        string? currentUserEmail,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid hostId,
        Guid configId,
        string? apiToken = null,
        CancellationToken cancellationToken = default);

    Task RemoveServiceAsync(
        Guid hostId,
        ExposedServiceConfig config,
        bool removeLmsRecord,
        string? apiToken = null,
        CancellationToken cancellationToken = default);

    Task DeleteDnsRecordAsync(
        Guid hostId,
        string zoneId,
        string recordId,
        string? apiToken = null,
        CancellationToken cancellationToken = default);

    Task ForgetSavedTokenAsync(Guid hostId, CancellationToken cancellationToken = default);
}
