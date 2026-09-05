// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Application.Interfaces;

public interface IMailRelayService
{
    Task<MailRelayDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<MailRelayPreflightResult> InspectPreflightAsync(
        string? cloudflareZoneId,
        CancellationToken cancellationToken = default);
    Task<MailRelayPreflightResult> RunPreflightAsync(
        string? cloudflareZoneId = null,
        CancellationToken cancellationToken = default);
    Task<MailRelaySetupPreview> PreviewSetupAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default);
    Task<MailRelayProvisioningJobSnapshot> StartProvisioningAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default);
    Task<MailRelayTestResult> SendTestAsync(
        MailRelayTestRequest request,
        CancellationToken cancellationToken = default);
    string GenerateClientPassword();
    Task<MailRelayClientSaveResult> SaveClientAsync(
        MailRelayClientSaveRequest request,
        CancellationToken cancellationToken = default);
    Task<MailRelayLegacySubmissionResult> ConfigureLegacySubmissionAsync(
        MailRelayLegacySubmissionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
    Task<MailRelayRemovalResult> RemoveAsync(
        MailRelayRemovalRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
    MailRelayProvisioningJobSnapshot? GetProvisioningJob(Guid jobId);
    MailRelayProvisioningJobSnapshot? GetLatestProvisioningJob();
}
