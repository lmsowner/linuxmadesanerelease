// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Application.Interfaces;

public interface IEdgeGatewayService
{
    Task<EdgeGatewayDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<EdgeGatewaySettingsEditor> GetSettingsEditorAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(EdgeGatewaySettingsEditor editor, CancellationToken cancellationToken = default);
    Task<EdgeGatewayRouteEditor> GetEditorAsync(Guid? routeId, CancellationToken cancellationToken = default);
    Task<Guid> SaveRouteAsync(EdgeGatewayRouteEditor editor, CancellationToken cancellationToken = default);
    Task DeleteRouteAsync(Guid routeId, CancellationToken cancellationToken = default);
    Task<EdgeGatewayDiagnosticResult> TestRouteAsync(Guid routeId, CancellationToken cancellationToken = default);
    Task<EdgeGatewayCaddyApplyResult> ApplyCaddyConfigurationAsync(CancellationToken cancellationToken = default);
    Task<EdgeGatewayCaddyApplyResult> RollbackCaddyConfigurationAsync(CancellationToken cancellationToken = default);
    Task<EdgeGatewayCaddyApplyResult> PanicDisableAllRoutesAsync(CancellationToken cancellationToken = default);
    Task<CloudflareValidationResult> ValidateCloudflareTokenAsync(string apiToken, bool saveToken, CancellationToken cancellationToken = default);
    Task ResetSetupAsync(CancellationToken cancellationToken = default);
    Task<EdgeGatewayCloudflareSetupResult> ProvisionCloudflareDomainAsync(string domainName, bool replaceExistingDnsRecord, CancellationToken cancellationToken = default);
    Task<EdgeGatewayCloudflareRelayRemovalResult> RemoveCloudflareDomainRelayAsync(string domainName, bool confirmed, CancellationToken cancellationToken = default);
    Task<EdgeGatewayCloudflareRouteSetupResult> ProvisionCloudflareRouteAsync(Guid routeId, bool replaceExistingDnsRecord, CancellationToken cancellationToken = default);
    Task<EdgeGatewayRemoteLmsRelaySetupResult> ProvisionRemoteLmsRelayAsync(string domainName, string hostname, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EdgeGatewayAuditEntry>> ListAuditEntriesAsync(EdgeGatewayAuditFilter filter, CancellationToken cancellationToken = default);
    Task<EdgeGatewayAuthCheckResult> EvaluateAuthAsync(EdgeGatewayAuthCheckContext context, CancellationToken cancellationToken = default);
    Task<EdgeGatewayTemporaryIpApprovalCompletionViewModel> ApproveTemporaryIpAsync(string token, CancellationToken cancellationToken = default);
    Task<string> BuildSafeReturnPathAsync(string targetUrl, CancellationToken cancellationToken = default);
    Task<bool> IsSafeReturnTargetAsync(string targetUrl, CancellationToken cancellationToken = default);
}
